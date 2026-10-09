using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Beam.Core.Phone;
using Beam.Core.Transfer;

namespace Beam.Core.Tests;

public class PhoneLinkTests
{
    private static HttpClient Client(TestNode node, bool withToken = true)
    {
        var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{node.Node.PhoneLink.Port}/{(withToken ? node.Node.PhoneLink.Token + "/" : "")}") };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) Mobile");
        return client;
    }

    // Browsers send Content-Length (fetch with a string body); HttpClient's JsonContent would use chunked encoding.
    private static StringContent Json(object value) => new(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json");

    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response)
    {
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
    }

    [Fact]
    public async Task PageNeedsTheSecretLink()
    {
        await using var pc = new TestNode("Office PC");
        pc.Node.PhoneLink.Start(0);
        using var client = Client(pc);

        var page = await client.GetStringAsync("");
        Assert.Contains("Send to this computer", page);
        var info = await JsonAsync(await client.GetAsync("api/info"));
        Assert.Equal("Office PC", info.GetProperty("device").GetString());

        using var stranger = Client(pc, withToken: false);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync("")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync("00000000000000000000000000000000/api/files")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await stranger.GetAsync(new string('a', 24) + "/api/files")).StatusCode);
    }

    [Fact]
    public async Task PhoneSendsFilesAfterApproval()
    {
        await using var pc = new TestNode("Office PC");
        pc.Node.PhoneLink.Start(0);
        using var client = Client(pc);
        var photo = new byte[3 * 1024 * 1024 + 17];
        Random.Shared.NextBytes(photo);
        var note = Encoding.UTF8.GetBytes("hello from the phone");
        File.WriteAllText(Path.Combine(pc.ReceiveFolder, "note.txt"), "already here");

        var offer = await JsonAsync(await client.PostAsync("api/offer", Json(new
        {
            files = new[] { new { name = "IMG_0001.HEIC", size = (long)photo.Length }, new { name = "../../note.txt", size = (long)note.Length } },
        })));
        Assert.True(offer.GetProperty("ok").GetBoolean());
        var request = Assert.Single(pc.Handler.Requests);
        Assert.Equal("iPhone", request.SenderName);
        Assert.Equal(2, request.FileCount);

        var id = offer.GetProperty("offerId").GetString();
        Assert.True((await JsonAsync(await client.PutAsync($"api/upload/{id}/0", new ByteArrayContent(photo)))).GetProperty("ok").GetBoolean());
        Assert.True((await JsonAsync(await client.PutAsync($"api/upload/{id}/1", new ByteArrayContent(note)))).GetProperty("ok").GetBoolean());
        // Everything arrived, so the transfer is closed and can't be written to again.
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"api/upload/{id}/1", new ByteArrayContent(note))).StatusCode);

        Assert.Equal(photo, File.ReadAllBytes(Path.Combine(pc.ReceiveFolder, "IMG_0001.HEIC")));
        Assert.Equal(note, File.ReadAllBytes(Path.Combine(pc.ReceiveFolder, "note (1).txt"))); // never overwrites, never escapes the folder
        Assert.Equal("already here", File.ReadAllText(Path.Combine(pc.ReceiveFolder, "note.txt")));
        Assert.Empty(Directory.GetFiles(pc.ReceiveFolder, "*.beampart"));

        var session = pc.Node.Transfers.Sessions.Single(s => s.Direction == TransferDirection.Receive);
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(2, session.GetSnapshot().CompletedFiles);
        await Wait.UntilAsync(() => pc.Node.History.Entries.Count == 1, because: "history entry");
    }

    [Fact]
    public async Task DeclinedOrIncompleteUploadsSaveNothing()
    {
        await using var pc = new TestNode("Office PC");
        pc.Node.PhoneLink.Start(0);
        using var client = Client(pc);

        pc.Handler.Decide = _ => IncomingDecision.Decline();
        var declined = await JsonAsync(await client.PostAsync("api/offer", Json(new { files = new[] { new { name = "a.jpg", size = 10L } } })));
        Assert.False(declined.GetProperty("ok").GetBoolean());

        pc.Handler.Decide = _ => IncomingDecision.Accept();
        var offer = await JsonAsync(await client.PostAsync("api/offer", Json(new { files = new[] { new { name = "b.jpg", size = 1000L } } })));
        var id = offer.GetProperty("offerId").GetString();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsync($"api/upload/{id}/0", new ByteArrayContent(new byte[10]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsync($"api/upload/{id}/0", new ByteArrayContent(new byte[1000]))).StatusCode);

        Assert.Empty(Directory.GetFiles(pc.ReceiveFolder));
        var failed = pc.Node.Transfers.Sessions.Where(s => s.Direction == TransferDirection.Receive).Select(s => s.State).ToList();
        Assert.Contains(TransferState.Declined, failed);
        Assert.Contains(TransferState.Failed, failed);
    }

    [Fact]
    public async Task PhoneDownloadsSharedFilesAndFolders()
    {
        await using var pc = new TestNode("Office PC");
        pc.Node.PhoneLink.Start(0);
        using var client = Client(pc);
        var report = pc.CreateFile("Report final.pdf", 2_500_000, 1);
        pc.CreateFile("Photos/a.jpg", 1000, 2);
        pc.CreateFile("Photos/Trip/b.jpg", 2000, 3);

        var empty = await JsonAsync(await client.GetAsync("api/files"));
        Assert.Equal(0, empty.GetProperty("files").GetArrayLength());

        var session = pc.Node.PhoneLink.Share(new[] { report, pc.SourcePath("Photos") });
        var files = (await JsonAsync(await client.GetAsync("api/files"))).GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(3, files.Count);
        Assert.Contains(files, f => f.GetProperty("name").GetString() == "Photos/Trip/b.jpg");

        foreach (var file in files)
        {
            using var response = await client.GetAsync("api/files/" + file.GetProperty("id").GetString());
            response.EnsureSuccessStatusCode();
            Assert.Equal(file.GetProperty("size").GetInt64(), (await response.Content.ReadAsByteArrayAsync()).Length);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
        }

        using (var pdf = await client.GetAsync("api/files/" + files.First(f => f.GetProperty("name").GetString()!.EndsWith(".pdf")).GetProperty("id").GetString()))
            Assert.Equal(File.ReadAllBytes(report), await pdf.Content.ReadAsByteArrayAsync());

        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(3, session.GetSnapshot().CompletedFiles);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/files/99")).StatusCode);
    }

    [Fact]
    public async Task PhoneDownloadsASharedFolderAsOneZip()
    {
        await using var pc = new TestNode("Office PC");
        pc.Node.PhoneLink.Start(0);
        using var client = Client(pc);
        var a = pc.CreateFile("Holiday/a.jpg", 1_500_000, 2);
        var b = pc.CreateFile("Holiday/Day 2/Фото.jpg", 2000, 3);
        var single = pc.CreateFile("notes.txt", 100, 4);

        var session = pc.Node.PhoneLink.Share(new[] { pc.SourcePath("Holiday"), single });
        var list = await JsonAsync(await client.GetAsync("api/files"));
        var folder = Assert.Single(list.GetProperty("folders").EnumerateArray().ToList());
        Assert.Equal("Holiday", folder.GetProperty("name").GetString());
        Assert.Equal(2, folder.GetProperty("fileCount").GetInt32());
        Assert.Equal(1_502_000, folder.GetProperty("size").GetInt64());
        var files = list.GetProperty("files").EnumerateArray().ToList();
        Assert.Equal(2, files.Count(f => f.GetProperty("folder").GetString() == folder.GetProperty("id").GetString()));
        Assert.Contains(files, f => f.GetProperty("name").GetString() == "notes.txt" && f.GetProperty("folder").ValueKind == JsonValueKind.Null);

        using var response = await client.GetAsync("api/folders/" + folder.GetProperty("id").GetString());
        response.EnsureSuccessStatusCode();
        Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Holiday.zip", response.Content.Headers.ContentDisposition?.FileNameStar);
        var bytes = await response.Content.ReadAsByteArrayAsync();
        Assert.Equal(response.Content.Headers.ContentLength, bytes.Length);
        using (var zip = new System.IO.Compression.ZipArchive(new MemoryStream(bytes)))
        {
            Assert.Equal(new[] { "Holiday/Day 2/Фото.jpg", "Holiday/a.jpg" }, zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            using var read = new MemoryStream();
            using (var s = zip.GetEntry("Holiday/a.jpg")!.Open()) s.CopyTo(read);
            Assert.Equal(File.ReadAllBytes(a), read.ToArray());
        }

        // The two folder files count as done; the single file is still waiting.
        var snapshot = session.GetSnapshot();
        Assert.Equal(2, snapshot.CompletedFiles);
        Assert.NotEqual(TransferState.Completed, session.State);
        using (await client.GetAsync("api/files/" + files.First(f => f.GetProperty("name").GetString() == "notes.txt").GetProperty("id").GetString())) { }
        Assert.Equal(TransferState.Completed, session.State);
        Assert.Equal(snapshot.TotalBytes, session.GetSnapshot().TransferredBytes);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("api/folders/5")).StatusCode);
    }

    [Fact]
    public async Task StoppingInvalidatesTheLink()
    {
        await using var pc = new TestNode("Office PC");
        pc.Node.PhoneLink.Start(0);
        var port = pc.Node.PhoneLink.Port;
        var oldToken = pc.Node.PhoneLink.Token;
        await pc.Node.PhoneLink.StopAsync();
        Assert.False(pc.Node.PhoneLink.IsRunning);

        pc.Node.PhoneLink.Start(port);
        Assert.NotEqual(oldToken, pc.Node.PhoneLink.Token);
        using var client = new HttpClient();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"http://127.0.0.1:{pc.Node.PhoneLink.Port}/{oldToken}/")).StatusCode);
    }

    [Fact]
    public async Task SharingCountsAsAFreeSend()
    {
        await using var pc = new TestNode("Office PC", pro: false);
        pc.Node.PhoneLink.Start(0);
        var file = pc.CreateFile("a.txt", 10);
        for (var i = 0; i < Licensing.FreeLimits.SendsPerDay; i++) pc.Node.PhoneLink.Share(new[] { file });
        var ex = Assert.Throws<TransferException>(() => pc.Node.PhoneLink.Share(new[] { file }));
        Assert.Equal(TransferErrorKind.SendLimitReached, ex.Error.Kind);
    }
}

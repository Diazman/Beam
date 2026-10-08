# Beam — product and marketing plan

_Created October 8, 2026. Owner: Diazman. Review this plan every month and move dates if needed._

_The code changes behind each step are in [ENGINEERING-PLAN.md](ENGINEERING-PLAN.md)._

**Goal:** earn money from Beam by first getting many people to use it for free, then selling a small,
clearly worth-it upgrade (Beam Pro) to some of them.

**One-line pitch:** *AirDrop for everyone — send files between any phone and any PC in seconds. No cable,
no account, no internet, no ads in the way.*

---

## 1. Where we are today (v1.1.0)

- **Windows app:** works, and is ready for the Microsoft Store.
  - Automatic discovery, encrypted transfers, resume after interruption, every file verified, history.
  - Trusted devices, Pro purchase through the Store.
  - Phone page: phones send and receive through the browser.
- **Store package:** has the real Partner Center identity. The Store submission is in progress.
- **Missing:** native phone apps, a website, translations, and marketing material.

---

## 2. Business model

### Phase A: free launch (first 3–6 months after the Windows Store release)
- **Everything is free and unlimited.** Remove the 5 MB/s speed limit and the 10-sends-per-day limit before launch.
- **Goals:** installs, good reviews, word of mouth.
- **The first ~1,000 users get free lifetime Pro** ("founding users"). They'll write reviews and recommend the app.

### Phase B: freemium (after the Android app has launched and been stable for ~2 months)
- **Free forever:** sending between two devices at full speed, with no daily limit, plus history, trusted
  devices, encryption and resume. Core transfers are never limited.
- **Beam Pro (paid):**
  1. **Automatic photo and video backup** from phone to PC over home Wi-Fi. This is the main reason to pay.
  2. **Direct connection anywhere:** works with no Wi-Fi network, and falls back to the normal network automatically.
  3. **Send to several devices at once.**
  4. **Browse your PC's files from your phone** and pull what you need.
  5. **No ads** in the phone apps.
- **Prices (start here, adjust later):**
  - $1.99/month, $9.99/year, or $19.99 lifetime.
  - **Regional prices** for Türkiye and Uzbekistan, set in each store.
- **Ads:** phone apps (free version) only.
  - One small banner on the main screen, never during a transfer.
  - Non-personalized, so there is no tracking prompt on iPhone.
  - The Windows app stays ad-free.
- **Purchases are per store.** Microsoft, Google and Apple each sell their own Pro; one doesn't carry over to another.

---

## 3. Features

### Must-have (free) — these decide whether people keep the app
| # | Feature | Platforms |
|---|---|---|
| F1 | Native Android app (history, trusted devices, encryption, resume, same protocol as Windows) | Android |
| F2 | Native iPhone app | iOS |
| F3 | One-tap sending: Share → Beam (phones), right-click → "Send with Beam" (Windows) | All |
| F4 | Pair once with a QR code, then devices recognize each other; trusted devices auto-accepted | All |
| F5 | Receive in the background with notifications | Android, Windows (iPhone: app must be open — iOS rule) |
| F6 | Send text, links and clipboard, not just files | All |
| F7 | Languages: English, Turkish, Russian, Uzbek (app + store listings) | All |

### Growth features — these make users bring new users
| # | Feature |
|---|---|
| G1 | Sending to someone without Beam: they get the web page (today's Phone page) with a "Get Beam — it's free" button |
| G2 | Invite a friend: both get 1 month of Pro (implemented with each store's offer/promo codes, as the store rules require) |
| G3 | Ask for a rating after the 3rd successful transfer (built-in review prompts on Windows, Android, iOS) |

### Pro features (paid, from Phase B)
| # | Feature |
|---|---|
| P1 | Automatic photo/video backup from phone to PC over home Wi-Fi |
| P2 | Direct connection without a Wi-Fi network, with automatic fallback to the normal network (Android ↔ PC: Wi-Fi Direct; iPhone ↔ PC: the PC creates its own Wi-Fi; phone ↔ phone: Wi-Fi Aware, Beta) |
| P3 | Send to several devices at once (already on Windows) |
| P4 | Browse the PC's files from the phone |
| P5 | No ads on phones |

---

## 4. Timeline

Weeks count from **Week 1 = October 12, 2026**. Dates are targets, not promises; testing on real phones
can move them.

### Stage 1 — Windows launch-ready (Weeks 1–3: Oct 12 – Nov 1)
| Step | Who | Done when |
|---|---|---|
| 1.1 Remove free speed and daily limits for the launch phase | Claude | Released in a new version |
| 1.2 "Send with Beam" in the Windows right-click menu (F3) | Claude | Works in installer and Store versions |
| 1.3 Rating prompt after the 3rd successful transfer (G3) | Claude | Shown once, never during a transfer |
| 1.4 Translate app to Turkish, Russian, Uzbek (F7) | Claude drafts, Diazman checks the Uzbek/Turkish | All screens translated |
| 1.5 Send text/links/clipboard between PCs (F6) | Claude | Works between two PCs |
| 1.6 Finish Partner Center: identity ✓, payout ✓, tax ✓, BeamPro add-on, listing in 4 languages, screenshots | Diazman (Claude prepares texts) | Submitted |
| 1.7 Privacy policy public URL (gist or website) | Diazman | Link added to the listing |
| 1.8 **Windows app live in the Microsoft Store** | Microsoft review | Store page link works |

### Stage 2 — Website and launch material (Weeks 3–5: Oct 26 – Nov 15)
| Step | Who | Done when |
|---|---|---|
| 2.1 Buy a domain (~$10–15/year) | Diazman | Domain registered |
| 2.2 Website: what Beam is, download buttons, 20-second demo, FAQ, privacy policy, 4 languages | Claude builds, Diazman publishes (GitHub Pages is free) | Live |
| 2.3 20-second demo video: 1 GB video phone → PC wirelessly vs. USB cable | Diazman records, Claude writes the script | Video ready in 9:16 and 16:9 |
| 2.4 Store listing search words (ASO) in 4 languages: "file transfer", "send files to PC", "phone to PC", "SHAREit alternative"… | Claude writes, Diazman pastes | Listings updated |
| 2.5 Social accounts: TikTok, Instagram, YouTube, Telegram channel ("Beam app") | Diazman | Accounts created, same name and logo |

### Stage 3 — Windows launch, free (Weeks 5–7: Nov 9 – Nov 29)
| Step | Who |
|---|---|
| 3.1 List on AlternativeTo.net as an alternative to SHAREit, AirDrop, LocalSend, Xender, Send Anywhere | Diazman |
| 3.2 Reddit posts (r/Windows11, r/software, r/privacy) — honest "I built this, feedback welcome" | Diazman (Claude drafts) |
| 3.3 Product Hunt launch and Hacker News "Show HN" (angle: no servers, no account, stays on your network) | Diazman (Claude drafts) |
| 3.4 Telegram tech channels in Uzbekistan and Türkiye (free posts first, then a few cheap paid posts) | Diazman |
| 3.5 University: ask friends to try it; pick 3–5 "Beam ambassadors" with free Pro | Diazman |
| 3.6 Reply to every review and message within 1–2 days | Diazman |

### Stage 4 — Android app (Weeks 4–14: Nov 2 – Jan 17)
Built with the same C# code as Windows (Avalonia + Beam.Core), so it uses the same encrypted protocol.
| Step | Who | Done when |
|---|---|---|
| 4.1 Android project, discovery, send/receive with Windows PCs | Claude | Test APK on GitHub; Diazman confirms on a real phone |
| 4.2 History, trusted devices, pairing by QR (F4), save to Downloads/Beam | Claude | Confirmed on phone |
| 4.3 Share → Beam from Gallery/any app (F3), send text/links (F6) | Claude | Confirmed on phone |
| 4.4 Background receiving with a notification (F5) | Claude | Receives with the app closed |
| 4.5 Translations (F7), rating prompt (G3) | Claude | Done |
| 4.6 Google Play developer account ($25 one-time) | Diazman | Account verified |
| 4.7 **Closed test: 12+ testers for 14 days** (Google rule for new personal accounts) | Diazman recruits friends/classmates | 14 days completed |
| 4.8 Play Store listing in 4 languages, screenshots, video | Claude prepares, Diazman uploads | Submitted |
| 4.9 **Android app live on Google Play** (free, no ads yet) | Google review | Store page link works |

### Stage 5 — Android launch and growth (Weeks 14–20: Jan 18 – Feb 28, 2027)
| Step | Who |
|---|---|
| 5.1 Announce everywhere: "Beam is now on Android — phone ↔ PC without cables" | Diazman |
| 5.2 Short videos: 2–3 per week on TikTok/Reels/Shorts, each solving one problem ("Photos from Android to Windows without a cable") | Diazman (Claude writes scripts) |
| 5.3 Contact 10–20 small tech YouTubers/bloggers (Turkish, Russian, Uzbek); offer free Pro codes to give away | Diazman (Claude writes the message) |
| 5.4 Web-link growth feature (G1): "Get Beam" button on the page people without the app see | Claude |

### Stage 6 — Pro features and freemium (Weeks 16–26: Feb 1 – Apr 11, 2027)
| Step | Who |
|---|---|
| 6.1 Automatic photo/video backup phone → PC (P1) | Claude |
| 6.2 Direct connection with automatic fallback, Android ↔ PC first (P2), marked Beta until tested on many devices | Claude builds, Diazman + testers test on real hardware |
| 6.3 Browse PC files from the phone (P4) | Claude |
| 6.4 Subscriptions + lifetime in all stores; regional prices; founding users keep lifetime Pro | Claude (code), Diazman (store setup) |
| 6.5 Banner ad in the free Android app (P5 removes it); update privacy policy and Play "Data safety" form | Claude + Diazman |
| 6.6 Invite-a-friend with store promo/offer codes (G2) | Claude + Diazman |
| 6.7 **Switch to freemium** (Phase B) | Diazman decides the date |

### Stage 7 — iPhone app (Weeks 20–32: Mar 1 – May 23, 2027)
| Step | Who |
|---|---|
| 7.1 Apple Developer Program ($99/year) | Diazman |
| 7.2 iPhone app from the same code; built on GitHub's Mac machines (no Mac needed) | Claude |
| 7.3 TestFlight testing on Diazman's and testers' iPhones | Diazman |
| 7.4 Local Network permission text, Files-app folder, share sheet (F3) | Claude |
| 7.5 Direct mode iPhone ↔ PC (PC creates Wi-Fi, iPhone joins) and iPhone ↔ iPhone (Wi-Fi Aware) — Beta | Claude + testers |
| 7.6 **iPhone app live on the App Store** | Apple review |

### Stage 8 — Paid growth (from Week 26, only if the numbers are good)
| Step | Who |
|---|---|
| 8.1 Small ad tests: Google App Campaigns, Apple Search Ads, Telegram channel ads — $5–10/day each | Diazman |
| 8.2 Keep only what brings users who stay (see section 6) | Diazman |

---

## 5. Marketing playbook (ongoing)

**Every week**
- Post 2–3 short videos (TikTok, Reels, Shorts). Each one shows one real problem solved in under 20 seconds.
- Reply to all reviews and messages.
- Post one useful tip in the Telegram channel.

**Every month**
- One "what's new" post with a short video.
- Contact 5 new bloggers or channels.
- Update store screenshots if the app changed.

**Message rules**
- Lead with the problem, not the technology: "Move photos from your phone to your PC in seconds."
- Always mention the three trust points: **no account, no internet needed, files stay between your devices.**
- Never claim things Beam doesn't do. Stores remove apps for misleading listings.

**Video ideas**
1. USB cable vs. Beam: same 1 GB video, side by side.
2. iPhone photos to a Windows PC without iTunes.
3. Send a file to a friend's phone that doesn't even have the app (web link).
4. Wi-Fi drops halfway — Beam continues where it stopped.
5. "Your files never go to a server" — privacy explained in 15 seconds.

---

## 6. What to measure (store dashboards are enough; no tracking inside the app)

| Metric | Where | Target |
|---|---|---|
| Rating | All stores | 4.5+ |
| Installs per week | Partner Center, Play Console, App Store Connect | Growing every month |
| Still using after 7 days | Play Console / App Store Connect retention | 30%+ |
| Uninstall rate | Play Console | Falling |
| Pro purchases (Phase B) | Stores | 1–5% of active users |
| Crashes | All stores | Fix any crash within a week |

**Rule:** if ratings or retention are poor, stop marketing spend and fix the app first.

---

## 7. Costs (known so far)

| Item | Cost |
|---|---|
| Microsoft Partner Center | Done |
| Google Play developer account | $25 one-time |
| Apple Developer Program | $99/year |
| Domain for the website | ~$10–15/year |
| Website hosting (GitHub Pages) | Free |
| Paid ads (Stage 8, optional) | Start at $5–10/day per channel |

Microsoft keeps 15% of app sales. Google and Apple keep 15% for small developers in their programs; check
each store's current terms when setting up. Ad networks pay per view and click, and amounts vary a lot by country.

---

## 8. Expectations

- Typical freemium apps convert about **1–5% of active users** into buyers. Income grows with users.
- **Subscriptions** (photo backup) bring recurring income; lifetime purchases bring one-time income.
- Expect **6–12 months** of steady work before meaningful income. The most common reason apps fail is that
  marketing stops after launch.

---

## 9. Decisions still open

| Decision | When |
|---|---|
| Exact launch prices and regional prices | Before Stage 6.4 |
| Date to switch from free launch to freemium | Stage 6.7 |
| Whether the phone ads earn enough to keep | 2 months after 6.5 |
| Website domain name | Stage 2.1 |

## 10. Risks

| Risk | Plan |
|---|---|
| Direct Wi-Fi doesn't work on some laptops/phones | Always fall back to the normal network; mark Beta until proven |
| iPhone can't receive in the background (iOS rule) | Say so clearly in the app; AirDrop-style background receiving isn't allowed for other apps |
| Store rejection | Follow each store's rules; fix and resubmit (usually days) |
| Low visibility at launch | AlternativeTo, Telegram, videos and students are free channels; keep posting weekly |
| Ads hurt reviews | Small banner only, never during transfers; Pro removes ads; remove ads if ratings drop |

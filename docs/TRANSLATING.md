# Translating Beam

Beam's interface is in English, Turkish (`tr`), Russian (`ru`) and Uzbek (`uz`, Latin script). The language follows
Windows unless the user picks one in **Settings → Language** (applied the next time Beam starts).

## How it works

- **The English text is the key.** Code writes `L.T("Send")`, `L.T("{0} declined the files.", name)` or
  `L.Plural(count, "{0} file", "{0} files")`; XAML writes `Text="{l:T 'Nearby computers'}"` (escape `'` as `\'`).
- **Catalogs** are `src/Beam.Core/Localization/<code>.json`: English → translation. A plural is an array of forms:
  Russian `[one, few, many]` (1 файл, 2 файла, 5 файлов); Turkish and Uzbek use one form ("5 dosya", "5 ta fayl").
- **Anything missing falls back to English**, so a new string never shows up blank.
- **The phone page** (`src/Beam.Core/Phone/phone.html`) carries its own small dictionary and follows the phone
  browser's language.
- **Store listings** are `packaging/store/LISTING.<code>.md`.

## Adding or changing text

1. Write the English text with `L.T` / `L.Plural` / `{l:T}` — whole sentences with `{0}` placeholders, never
   sentences glued together from translated pieces. The first argument must be a plain string literal on one line.
2. `python3 tools/l10n.py missing tr` (and `ru`, `uz`) prints what each catalog lacks; translate and add it.
3. `python3 tools/l10n.py check` must report 0 problems. The same check runs as `LocalizationTests` in CI: every
   text translated, same placeholders, no unused entries.
4. `LanguageUiTests` renders the main screens in each language (also at the smallest window size) to
   `artifacts/screenshots/language-*.png` — look at them for text that doesn't fit.

## Adding a language

Add it to `L.Languages` (code and its own name), a plural rule in `L.PluralForm` if needed, a catalog
`<code>.json`, the phone page's `STRINGS`, a `<Resource Language=…>` in `packaging/msix/AppxManifest.xml` and a
Store listing.

## Style

Short, friendly, plain words as in Windows 11 itself. A computer name can be anything ("Diaz's laptop"), so avoid
grammar that depends on how the name ends (Turkish/Uzbek suffixes, Russian gender): "{0} adlı bilgisayar",
"{0} kompyuteriga", «Компьютер «{0}» отклонил файлы.»

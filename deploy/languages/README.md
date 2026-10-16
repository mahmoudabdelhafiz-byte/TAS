# Language packs

Yardsyncs speaks English and Arabic in the portal, and English, Arabic, Urdu and Hindi in the drivers' app and in its
messages (SMS, email, push). A language pack adds a language, or corrects the texts of one Yardsyncs already has,
without a new version.

## Add a language

1. Copy `template.json` to `<code>.json` in this folder, where `<code>` is the two-letter language code
   (`fr.json`, `tl.json`, `bn.json`).
2. Set `code`, `name` (the language's own name, e.g. `Français`) and `direction` (`ltr`, or `rtl` for languages
   written right to left).
3. Translate the texts. There are three parts, and you can do any of them:
   - `portal`: the web portal. With a third portal language, the language link becomes a list.
   - `app`: Yardsyncs mobile, the drivers' app (it picks up the pack when it next starts).
   - `messages`: SMS, emails and push messages. Keep every `{placeholder}` as it is; SMS texts should stay short.
   Delete any text you don't translate: it shows in English.
4. Restart the app: `docker compose restart app`. `GET /api/ops/status` lists the packs in use under `languagePacks`.

Drivers and users can then be given the language as their preferred language.

## Correct a built-in language

A pack named `ar.json`, `ur.json` or `hi.json` replaces only the texts it contains. Use it to apply a native speaker's
review of the wording.

## Mistakes

A pack with a mistake stops the app from starting, with the file and what is wrong, so a broken text never reaches
a driver. Examples: a message that uses a `{placeholder}` it doesn't have, or a `code` that doesn't match the file name.
Fix the file and start again; `docker compose logs app` shows the message.

`template.json` and this file are not packs and are never loaded.

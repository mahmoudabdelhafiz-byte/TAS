# Demo portal

A copy of the portal for showing customers: the real screens, with sample data answered in the browser.
There is no API, database or background job behind it, so it runs on any web host, including shared hosting.
Nothing is saved: forms that save show success, and a page reload brings the sample data back.

## Build

```
cd web
npm install
npm run build:demo        # → web/dist-demo/
```

The folder holds `index.html`, `app.js`, `app.css` and `.htaccess`. Paths are relative, so it works at the
root of a site or in a sub-folder such as `https://example.com/yardsyncs-demo/`. The screens use `#/…` addresses,
so the host needs no rewrite rules.

## Put it on shared hosting (cPanel)

1. Zip the contents of `web/dist-demo/` (the files, not the folder around them).
2. In cPanel open **File Manager** → `public_html` → **+ Folder**, name it `yardsyncs-demo`.
3. Open the new folder → **Upload** → choose the zip → back in the folder, right-click the zip → **Extract**, then delete the zip.
4. Open `https://<your domain>/yardsyncs-demo/`. To show `.htaccess` in File Manager turn on **Settings → Show hidden files**.

For an address of its own (`demo.<your domain>`), make the sub-domain in cPanel → **Domains** and extract the zip into its folder.
The page tells search engines not to index it. To keep it private, protect the folder in cPanel → **Directory Privacy**.

## Using it

The sign-in page lists the roles; one click signs in (any password works when typed):

| Role | Email | Shows |
|---|---|---|
| Terminal admin | admin@demo-terminal.example | everything: slot board, control room, KPIs, set-up, gate, safety, driver records |
| Planner | planner@demo-terminal.example | capacity, slot board, control room, KPIs |
| Gate clerk | clerk@demo-terminal.example | gate board, trouble desk, inspections (in Arabic) |
| Trucking company | dispatcher@alnoor.example | booking, tracking containers, bulk upload, own report and drivers |
| System admin | root@yardsyncs.example | the whole installation: operators, terminals, users |

Good things to try: book a truck with BL `MEDUJD260001` (one container is on customs hold and is refused, one has
storage charges), the control room map, **Ask the assistant**, switching to Arabic (top right).

Excel downloads say they are not in the demo. The driver app is not part of it.

## Sample data

`web/demo/mock.mjs` holds the answers (one day at the fictional *Demo Container Terminal*); `web/demo/demo.js` answers
the portal's `/api/…` calls from it and moves every date to the day the demo is shown. When a new screen is added,
add its answers to `mock.mjs`; CI builds the demo so it keeps compiling.

# Security and personal data

## Accounts and access

- Every call needs a signed-in account; each role sees only its own terminal (an operator admin: the terminals of its
  operator) or its own company. Drivers' app tokens open only the driver's own appointments.
- Passwords are stored hashed (PBKDF2); links to set a password work once and expire; five wrong passwords lock the
  account for a few minutes; sign-in is limited per client address. Deactivating a user ends their access at once.
- Drivers sign in with a code by SMS: 6 digits, five tries, a few codes an hour and per day; one phone at a time.
- The gate code in the app changes every 30 seconds, so a photo of it can't be reused later.

## Data protection

- HTTPS everywhere; strict browser security headers.
- Secrets (keys, passwords, provider accounts) are settings on the server, never in the code.
- The database and its backups stay where the installation runs: at the terminal, or in our cloud service in a Saudi
  region.
- Every version is scanned for known vulnerabilities in its software before release.

## Personal data (Saudi PDPL)

| About | What | Why | Kept |
|---|---|---|---|
| Portal users | Name, email, mobile, role, sign-ins | Accounts, audit | While the account exists |
| Drivers | Name, ID / iqama number, mobile, language | Identify the driver at booking and gate; messages | While the company keeps the driver |
| Drivers | Positions, only from "I am leaving now" until leaving the terminal | Arrival time, check-in, directions in the yard | 30 days |
| Drivers and users | Where messages went | Reminders, alerts, codes | 180 days |

The app tells drivers what it shares and asks before the first trip. A driver's company admin or a terminal admin can
give the driver a copy of everything held about them, or erase it (the visits stay without the person). Old
positions, message addresses and sessions are removed automatically every day.

Messages go through the chosen providers (SMS, email, push); the optional AI assistant uses Anthropic's API. Each
terminal decides which providers it uses and names them in its privacy notice.

## Reporting a problem

Security problems: write to the terminal's IT team or to us; please don't publish details before they are fixed.

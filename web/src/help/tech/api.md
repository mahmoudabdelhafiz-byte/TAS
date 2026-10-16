# API for trucking companies and integrators

Everything the portal does goes through the same HTTPS API, so a trucking company's dispatch system or a port
community system can book and follow visits directly. Ask the terminal for an account with the right role (a company
user for booking, a gate clerk account for an automated gate).

## Signing in

```
POST /api/auth/login        {"email": "...", "password": "..."}
→ {"accessToken": "...", "accessTokenExpiresAt": "...", "refreshToken": "...", "refreshTokenExpiresAt": "...", "user": {...}}
```

Send `Authorization: Bearer <accessToken>` with every call. The access token lasts 30 minutes; get a new pair with
`POST /api/auth/refresh {"refreshToken": "..."}` (each refresh token works once). Sign-in is limited per client
address; five wrong passwords lock the account for a few minutes.

## Main calls

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/terminals` | Terminals the account can work at |
| `GET` | `/api/terminals/{id}/categories` | Move types (for example `IMP_PICKUP`, `EXP_DROP`, `MTY_RETURN`) |
| `GET` | `/api/terminals/{id}/availability?date=2026-10-20&category=IMP_PICKUP` | Slots of a local day with free places |
| `POST` | `/api/terminals/{id}/containers/track` | `{"blNo": "..."}` or `{"containers": [...]}`: status of each container. Books nothing |
| `POST` | `/api/terminals/{id}/appointments` | Book: `{"slotId": 123, "items": [{"category": "IMP_PICKUP", "containerNo": "MSCU1234566"}], "truckId": 7, "driverId": 9}` |
| `GET` | `/api/terminals/{id}/appointments?date=2026-10-20` | The day's appointments |
| `GET` | `/api/terminals/{id}/appointments/{no}` | One appointment with its checks and "ready to go" light |
| `POST` | `/api/terminals/{id}/appointments/{no}/reschedule` | `{"slotId": 124}` |
| `POST` | `/api/terminals/{id}/appointments/{no}/cancel` | `{"reason": "..."}` |
| `PUT` | `/api/terminals/{id}/appointments/{no}/assignment` | `{"truckId": 7, "driverId": 9}` |
| `POST` | `/api/terminals/{id}/appointments/{no}/revalidate` | Check again with the TOS now |
| `POST` | `/api/terminals/{id}/appointments/bulk?dryRun=true` | Upload an `.xlsx` or `.csv` (form field `file`); `dryRun=false` books |
| `GET` / `POST` | `/api/companies/{id}/trucks`, `/api/companies/{id}/drivers` | The company's fleet |

Times are ISO 8601 with offset; dates in query strings are the terminal's local dates.

## Answers and errors

| Status | Meaning |
|---|---|
| `200` / `201` | Done (`201` with the new appointment) |
| `400` | The request is wrong; `errors` lists why |
| `401` | Not signed in, or the token expired: refresh |
| `403` | The account may not do this |
| `404` | Not found (also for another company's appointments) |
| `409` | No capacity left, or something changed in the meantime: reload and try again |
| `422` | Not cleared: a TOS or internal check failed; `checks` lists each one |

Errors are `application/problem+json` with `detail` and, where useful, `errors` and `checks`.

## Gate systems

An automated gate posts what it reads at an entry lane and gets a decision:

```
POST /api/terminals/{id}/gate/arrivals   {"laneCode": "IN-1", "plateRead": "1234 ABJ", "qrToken": "...", "containerReads": ["MSCU1234566"]}
→ admit, or refer to the trouble desk with the reason (EARLY, LATE, NOT_CLEARED, BLOCKED, ALREADY_IN, CANCELLED, NO_APPOINTMENT)
POST /api/terminals/{id}/gate/in    and    POST /api/terminals/{id}/gate/out
```

## Health

`GET /health/live` (the process answers) and `GET /health/ready` (the database answers and is up to date: 200 or 503)
need no sign-in.

# Yardsyncs lane agent (edge mode)

For terminals whose Yardsyncs runs in the cloud and that have automatic lanes (Yardsyncs Autogate). The agent runs at
the terminal, next to the lanes. Lane devices talk to it instead of the cloud; it passes everything through while the
cloud answers and runs the automatic lanes itself, from a snapshot, when the link drops. When the link is back it sends
what it decided, and the visits are recorded at the times the trucks went through. The technical page *Connecting lane
devices* explains what it does and doesn't do offline.

## Set up

1. In the portal, **Lanes → Gate systems**: add "Lane agent" and copy its key.
2. On a small Linux machine in the gate network (2 CPU, 2 GB RAM, Docker), next to this file:

   ```sh
   cat > .env <<'END'
   EDGE_CLOUD_URL=https://yardsyncs.example.com
   EDGE_API_KEY=ysg_…
   EDGE_TERMINAL_ID=1
   END
   docker compose up -d
   curl http://localhost:8090/edge/status     # "online": true, a snapshot time
   ```

3. Point every lane device (or its vendor adapter) at `http://<agent>:8090` instead of the cloud address. The device
   keys don't change.

Keep port 8090 inside the gate network: the devices are the only ones that need it. The agent keeps its snapshot
and the passages it still has to send in the `edge-data` volume, so a restart loses nothing.

## Settings

| Setting | Default | |
|---|---|---|
| `Edge__CloudUrl` | — | The cloud installation |
| `Edge__ApiKey` | — | The gate system key (keep it in `.env`, never in the repository) |
| `Edge__TerminalId` | — | The terminal's id |
| `Edge__TimeoutSeconds` | 3 | No answer from the cloud within this: the agent decides |
| `Edge__SnapshotMinutes` | 5 | How often the snapshot is refreshed while online |
| `Edge__SettleSeconds` / `Edge__WindowSeconds` | 3 / 30 | As the cloud's lane controller |

## Monitoring

`GET /edge/status` → `online`, `lastOnlineAt`, `snapshotTakenAt`, `toSend`. Alert when `online` is false for more
than a few minutes, or when the snapshot is older than an hour (the agent can't reach the cloud to refresh it).

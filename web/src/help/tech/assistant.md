# The AI assistant

The assistant is a chat panel on every page of the portal. Users ask in Arabic or English how to do something, or
about their own work ("is A-1234 ready to go?", "free hours tomorrow for empty returns?", "which of these containers
can I collect?"), and get a short answer with links to the right screen.

## What it can and can't do

- It answers from the Yardsyncs guides, in the user's language.
- With live data switched on, it can **read** what the user may already see in the portal: their appointments,
  free hours, container status, the terminal's move types and, for terminal staff, today's gate and yard figures.
- It **never changes anything**: no booking, cancelling, moving or messages. It tells the user where to do it.
- It can be wrong. For anything that matters, the screen is the reference.

## How it works

The portal sends the conversation to the Yardsyncs server, which adds the guides and the user's context and asks
Anthropic's Claude model (through the Anthropic API). When the model needs live data it asks the server, which looks
it up with the user's own permissions and passes back only the result. Conversations are not stored by Yardsyncs;
only a count of questions and tokens per user and day is kept, for the daily limit and the cost.

## Data and privacy

Questions and the data the assistant looks up (for example an appointment with a truck plate and a driver's name)
are sent to Anthropic, outside the Kingdom. Anthropic does not use API data to train its models; see Anthropic's
data retention policy for how long it is kept. A terminal that does not want personal data to leave can:

- switch off live data (`Assistant__LiveData=false`): the assistant then only explains how Yardsyncs works, or
- leave the assistant off (no API key) — the default.

Name Anthropic as a processor in the privacy notice when the assistant is on.

## Settings

| Setting | Default | Meaning |
|---|---|---|
| `Assistant__ApiKey` | (empty: off) | Anthropic API key |
| `Assistant__Model` | `claude-sonnet-5-5` | The model |
| `Assistant__LiveData` | `true` | Allow read-only look-ups of the user's data |
| `Assistant__DailyQuestionsPerUser` | `60` | Questions per user per day |
| `Assistant__BaseUrl` | `https://api.anthropic.com` | Another address for the same API, for example a company gateway |

The number of questions and tokens used today is in `GET /api/ops/status`.

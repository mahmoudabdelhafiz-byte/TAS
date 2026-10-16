// A lane device simulator and reference adapter for the Yardsyncs device API (see "Connecting lane devices" in the
// technical documentation). It acts as a plate camera and a barrier: it sends one read, reports status every 30 s, and
// carries out the barrier's commands, confirming each one. Node 18+, no packages needed.
//
//   YARDSYNCS_URL=https://yard.example.com CAMERA_KEY=ysd_… BARRIER_KEY=ysd_… node simulator.mjs "1234 ABJ" [MSCU1234566]

const base = (process.env.YARDSYNCS_URL ?? 'http://localhost:8080').replace(/\/$/, '');
const cameraKey = process.env.CAMERA_KEY;
const barrierKey = process.env.BARRIER_KEY;
const [plate = '1234 ABJ', container] = process.argv.slice(2);

async function call(key, method, path, body) {
  const response = await fetch(`${base}${path}`, {
    method,
    headers: { 'X-Device-Key': key, ...(body ? { 'Content-Type': 'application/json' } : {}) },
    body: body ? JSON.stringify(body) : undefined,
  });
  const text = await response.text();
  const data = text ? JSON.parse(text) : null;
  if (!response.ok) throw new Error(`${method} ${path}: ${response.status} ${data?.detail ?? text}`);
  return data;
}

if (cameraKey) {
  const me = await call(cameraKey, 'GET', '/api/devices/me');
  console.log(`Camera ${me.deviceCode} at lane ${me.laneCode}, terminal ${me.terminalCode}`);
  const read = await call(cameraKey, 'POST', '/api/devices/reads', {
    plate, plateConfidence: 95, containers: container ? [{ number: container, position: 'FRONT' }] : [],
  });
  console.log(`Read stored: plate ${read.data.plate}${read.data.warnings.length ? ` (${read.data.warnings.join(' ')})` : ''}`);
}

if (barrierKey) {
  const me = await call(barrierKey, 'GET', '/api/devices/me');
  console.log(`Barrier ${me.deviceCode} at lane ${me.laneCode}: waiting for commands (Ctrl+C to stop)`);
  const done = new Set();
  const status = () => call(barrierKey, 'POST', '/api/devices/status', { status: 'Ok' }).catch(e => console.error(e.message));
  await status();
  setInterval(status, 30_000);
  for (;;) {
    try {
      for (const c of await call(barrierKey, 'GET', '/api/devices/commands')) {
        if (done.has(c.commandId)) continue;
        if (new Date(c.expiresAt) < new Date()) continue; // too late: never act on an expired command
        console.log(`${new Date().toISOString()} ${c.command === 'Open' ? 'Barrier OPEN' : c.command === 'Close' ? 'Barrier CLOSED' : c.command}`);
        await call(barrierKey, 'POST', `/api/devices/commands/${c.commandId}/ack`, { done: true });
        done.add(c.commandId);
      }
    } catch (e) {
      console.error(e.message);
    }
    await new Promise(r => setTimeout(r, 1000));
  }
}

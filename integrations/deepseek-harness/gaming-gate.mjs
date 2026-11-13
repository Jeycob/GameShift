import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { randomUUID } from 'node:crypto';
import { setTimeout as sleep } from 'node:timers/promises';

export const name = 'gameshift-gaming-gate';
export const inject = ['llm'];

function acquireWriterLock(lockPath) {
  // Serialize ALL new lock claims, including dead-PID recovery. Checking a PID
  // and unlinking without this guard can let two starters delete each other's
  // freshly acquired lock. A crashed claim itself fails closed for inspection.
  const claimPath = lockPath + '.claim';
  let claim;
  try { claim = fs.openSync(claimPath, 'wx'); }
  catch (error) { throw new Error('GameShift gate is already starting, or a stale harness.lock.claim needs inspection. Stop all Harness instances before removing a stale claim.', { cause: error }); }
  try {
    fs.writeFileSync(claim, String(process.pid));
    let writer;
    try { writer = fs.openSync(lockPath, 'wx'); }
    catch (error) {
      if (error.code !== 'EEXIST') throw error;
      // Accept the legacy plain-PID format, but never guess at corrupt records.
      const fd = fs.openSync(lockPath, 'r');
      let text;
      try {
        if (fs.fstatSync(fd).size > 32) throw new Error('Invalid writer lock.');
        text = fs.readFileSync(fd, 'utf8').trim();
      } finally { fs.closeSync(fd); }
      if (!/^[1-9]\d*$/.test(text) || !Number.isSafeInteger(Number(text)))
        throw new Error('Invalid writer lock.');
      let definitelyDead = false;
      try { process.kill(Number(text), 0); }
      catch (probeError) {
        // Access denied and every unknown error mean NOT proven dead. PID reuse
        // may therefore require manual inspection; it can never grant two owners.
        definitelyDead = probeError.code === 'ESRCH';
      }
      if (!definitelyDead) throw new Error('Writer is alive or its state cannot be verified.');
      fs.unlinkSync(lockPath);
      writer = fs.openSync(lockPath, 'wx');
    }
    try { fs.writeFileSync(writer, String(process.pid)); }
    catch (error) { fs.closeSync(writer); fs.unlinkSync(lockPath); throw error; }
    return writer;
  } finally {
    fs.closeSync(claim);
    fs.unlinkSync(claimPath);
  }
}

// One Harness process per control directory. Multiple processes require a broker;
// refusing a second writer prevents an idle ACK from hiding another busy client.
export class GamingGate {
  constructor(directory, { pollMilliseconds = 1000 } = {}) {
    this.directory = directory;
    this.active = 0;
    this.closed = false;
    this.pollMilliseconds = pollMilliseconds;
    this.state = { Version: 1, CommandId: '', Blocked: false };
    fs.mkdirSync(directory, { recursive: true });
    this.lockPath = path.join(directory, 'harness.lock');
    try {
      this.lock = acquireWriterLock(this.lockPath);
    } catch (error) {
      throw new Error('GameShift gate already has a writer, or its lock cannot be safely recovered. Stop other Harness instances before inspecting stale harness.lock / harness.lock.claim files.', { cause: error });
    }
    try { this.refresh(); }
    catch (error) { this.close(); throw error; }
    this.timer = setInterval(() => { try { this.refresh(); } catch { this.state.Blocked = true; } }, pollMilliseconds);
    this.timer.unref();
  }
  refresh() {
    if (this.closed) return;
    try {
      const file = path.join(this.directory, 'control.json');
      const fd = fs.openSync(file, 'r');
      let command;
      try {
        if (fs.fstatSync(fd).size > 65536) throw new Error('Oversized control');
        command = JSON.parse(fs.readFileSync(fd, 'utf8'));
      } finally { fs.closeSync(fd); }
      if (command.Version !== 1 || typeof command.CommandId !== 'string' || !command.CommandId || typeof command.Blocked !== 'boolean')
        throw new Error('Invalid control');
      this.state = command;
    } catch (error) {
      // A missing file on first run is passive. Deleting an existing hold is NOT
      // an implicit resume; malformed/transient reads fail closed as well.
      if (!(error.code === 'ENOENT' && !this.state.CommandId && !this.state.Blocked))
        this.state = { Version: 1, CommandId: '', Blocked: true };
    }
    this.ack();
  }
  ack() {
    if (this.closed) return;
    const file = path.join(this.directory, 'harness-ack.json');
    const temp = file + '.' + randomUUID() + '.tmp';
    try {
      fs.writeFileSync(temp, JSON.stringify({ Version: 1, CommandId: this.state.CommandId,
        Blocked: this.state.Blocked, ActiveStreams: this.active, ObservedAt: new Date().toISOString() }));
      fs.renameSync(temp, file);
    } finally { try { fs.unlinkSync(temp); } catch { /* Already renamed. */ } }
  }
  async wait(signal) {
    while (true) {
      signal?.throwIfAborted();
      if (this.closed) throw new Error('GameShift gate was disposed.');
      this.refresh();
      if (!this.state.Blocked) return;
      await sleep(Math.min(this.pollMilliseconds, 250), undefined, { signal });
    }
  }
  stream(next, signal) {
    const gate = this;
    return (async function* () {
      await gate.wait(signal);
      // No await between the final gate check and busy ACK, so a newly blocked
      // command cannot be acknowledged idle while this request is entering.
      gate.active++;
      try {
        gate.ack();
        signal?.throwIfAborted();
        yield* await next();
      } finally { gate.active--; gate.refresh(); }
    })();
  }
  close() {
    if (this.closed) return;
    this.closed = true;
    clearInterval(this.timer);
    try { fs.closeSync(this.lock); } catch { /* Constructor failure. */ }
    try { fs.unlinkSync(this.lockPath); } catch { /* Diagnostic file only. */ }
    // Keep the control command. Never release a persistent gaming hold on exit.
  }
}

export function apply(ctx, config = {}) {
  const providers = new Set(config.providers ?? ['strata1']);
  const directory = config.directory ?? path.join(process.env.LOCALAPPDATA ?? path.join(os.homedir(), '.local', 'share'), 'GameShift');
  const gate = new GamingGate(directory);
  ctx.on('agent/pre-step', async ({ modelSelection, signal }, next) => {
    if (providers.has(modelSelection.provider)) await gate.wait(signal);
    return next();
  }, { prepend: true });
  // Stay INSIDE the turn-queue's prepended stream middleware: queued requests
  // are not active inference. Counting them as active could prevent unloading
  // forever while the queue owner waits at the gaming pre-step barrier.
  ctx.on('llm/stream', (options, next) => providers.has(options.provider)
    ? gate.stream(next, options.signal) : next(), { global: true });
  ctx.effect(() => () => gate.close(), 'gameshift-gaming-gate.dispose');
  return gate;
}

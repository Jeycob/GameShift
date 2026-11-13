import { test } from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { setTimeout as sleep } from 'node:timers/promises';
import { GamingGate, apply } from './gaming-gate.mjs';

function fixture(t) {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'gameshift-test-'));
  const gate = new GamingGate(dir, { pollMilliseconds: 30 });
  t.after(() => { gate.close(); fs.rmSync(dir, { recursive: true, force: true }); });
  const command = (blocked, id = 'test') => fs.writeFileSync(path.join(dir, 'control.json'), JSON.stringify({ Version: 1, CommandId: id, Blocked: blocked }));
  const ack = () => JSON.parse(fs.readFileSync(path.join(dir, 'harness-ack.json')));
  return { dir, gate, command, ack };
}
test('first run passive, blocks on command, ACK matches', async t => {
  const { gate, command, ack } = fixture(t);
  assert.equal(ack().Blocked, false);
  command(true); gate.refresh();
  assert.equal(ack().CommandId, 'test'); assert.equal(ack().Blocked, true);
  const abort = new AbortController();
  const waiting = gate.wait(abort.signal);
  abort.abort(); await assert.rejects(waiting, { name: 'AbortError' });
});
test('stream is lazy, drains current response but blocks next', async t => {
  const { gate, command, ack } = fixture(t);
  let calls = 0;
  const stream = gate.stream(async function* () { calls++; yield 1; yield 2; });
  assert.equal(calls, 0); assert.equal(ack().ActiveStreams, 0);
  assert.deepEqual(await stream.next(), { value: 1, done: false });
  command(true, 'gaming'); gate.refresh(); assert.equal(ack().ActiveStreams, 1);
  assert.equal((await stream.next()).value, 2); await stream.next();
  assert.equal(ack().ActiveStreams, 0);
  let started = false;
  const second = gate.stream(async function* () { started = true; yield 3; });
  const pending = second.next(); await sleep(40); assert.equal(started, false);
  command(false, 'resume'); assert.equal((await pending).value, 3); await second.return();
  assert.equal(ack().ActiveStreams, 0);
});
test('malformed and deleted control fail closed, valid resume recovers', t => {
  const { gate, dir, command, ack } = fixture(t);
  command(true); gate.refresh();
  fs.unlinkSync(path.join(dir, 'control.json')); gate.refresh(); assert.equal(ack().Blocked, true);
  fs.writeFileSync(path.join(dir, 'control.json'), '{'); gate.refresh(); assert.equal(ack().CommandId, '');
  command(false, 'fixed'); gate.refresh(); assert.equal(ack().Blocked, false);
});
test('second writer rejected; dispose keeps hold', t => {
  const { gate, dir, command } = fixture(t);
  assert.throws(() => new GamingGate(dir), /already has a writer/);
  command(true); gate.close();
  assert.equal(JSON.parse(fs.readFileSync(path.join(dir, 'control.json'))).Blocked, true);
});
test('failed stream releases active count', async t => {
  const { gate, ack } = fixture(t);
  const stream = gate.stream(() => { throw new Error('backend'); });
  await assert.rejects(stream.next(), /backend/); assert.equal(ack().ActiveStreams, 0);
});
test('plugin hooks only selected provider and cleans up', async t => {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(), 'gameshift-hooks-'));
  t.after(() => fs.rmSync(dir, { recursive: true, force: true }));
  const hooks = new Map(); let dispose;
  const gate = apply({ on: (name, fn) => hooks.set(name, fn), effect: fn => { dispose = fn(); } }, { directory: dir, providers: ['strata1'] });
  try {
    let calls = 0;
    await hooks.get('agent/pre-step')({ modelSelection: { provider: 'other' } }, () => calls++);
    assert.equal(calls, 1);
    const result = hooks.get('llm/stream')({ provider: 'other' }, () => 'other');
    assert.equal(result, 'other');
    assert.equal(gate.closed, false);
  } finally { dispose(); }
  assert.equal(gate.closed, true);
});
test('same agent continues after gaming without replaying tools or creating a task', async t => {
  const { gate, command } = fixture(t);
  const history = ['original user request'];
  let toolCalls = 0;
  const originalSession = { id: 'original-session', history };
  const run = (async () => {
    for await (const token of gate.stream(async function* () { yield 'first response'; })) originalSession.history.push(token);
    toolCalls++; history.push('tool result');
    command(true, 'game');
    await gate.wait(); // The original run remains here; no retry prompt is sent.
    for await (const token of gate.stream(async function* () { yield 'follow-up using tool result'; })) originalSession.history.push(token);
    return originalSession;
  })();
  await sleep(40);
  assert.equal(toolCalls, 1);
  assert.deepEqual(history, ['original user request', 'first response', 'tool result']);
  command(false, 'strata-ready');
  const result = await run;
  assert.equal(result, originalSession);
  assert.equal(toolCalls, 1);
  assert.equal(result.history.at(-1), 'follow-up using tool result');
});
test('several original waiting requests resume once and cancellation stays cancelled', async t => {
  const { gate, command } = fixture(t);
  command(true); gate.refresh();
  const abort = new AbortController();
  let cancelledCalls = 0;
  const cancelled = gate.stream(async function* () { cancelledCalls++; yield 'wrong'; }, abort.signal).next();
  abort.abort(); await assert.rejects(cancelled, { name: 'AbortError' });
  const calls = [];
  const jobs = Array.from({ length: 5 }, (_, id) => (async () => {
    const values = [];
    for await (const token of gate.stream(async function* () { calls.push(id); yield id; })) values.push(token);
    return values;
  })());
  await sleep(40); assert.equal(calls.length, 0);
  command(false, 'ready');
  assert.deepEqual(await Promise.all(jobs), [[0], [1], [2], [3], [4]]);
  assert.equal(new Set(calls).size, 5);
  assert.equal(cancelledCalls, 0);
});

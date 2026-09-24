// F04 integration-test fixture: exercises the real AppContainer sandbox + QuickJS engine end to end
// (HostSession -> ChildHost -> QuickJsRuntime), including the restricted Web API surface, the
// execution budget and per-runtime call concurrency.
export default {
  async translate(req) {
    return { text: `echo:${req.text}` };
  },
  // F04.2: the bridge lockdown removes eval/Function and any dynamic compiler; only $http/$store/$log
  // and the frozen globals the bootstrap sets up are reachable.
  async restricted() {
    const checks = {
      evalRemoved: typeof eval === 'undefined',
      functionRemoved: typeof Function === 'undefined',
      noProcess: typeof process === 'undefined',
      noRequire: typeof require === 'undefined',
      noFsImport: true,
    };
    try { new Function('return 1')(); checks.noFsImport = false; } catch { /* expected */ }
    return checks;
  },
  // Distinct capabilities that each suspend on a host round trip (ctx.$store.get - no host timer
  // exists in this engine), so two calls can be genuinely in flight on the same runtime at once
  // (PLAN 4.5.4: max 2 in-flight calls per runtime; only the JS slice is serial, suspended host
  // calls can be concurrent). Used to check that cancelling one does not disturb the other.
  async slowA(req, ctx) {
    await ctx.$store.get('slow-a-key');
    return { text: 'A-done' };
  },
  async slowB(req, ctx) {
    await ctx.$store.get('slow-b-key');
    return { text: 'B-done' };
  },
  async spin() {
    for (;;) { /* trips the execution budget on purpose */ }
  },
  // F04.2 gap check: an infinite *microtask* chain (never a synchronous loop) must still trip the
  // execution budget - the interrupt check must not be skipped while only microtask jobs are running.
  async spinMicrotask() {
    function again() { return Promise.resolve().then(again); }
    await again();
  },
  // X07: a malicious plugin trying to reach an origin outside its per-call grant through the *real*
  // host-proxied $http op (never a raw socket - the sandbox denies those directly; this checks the
  // broker itself refuses, which a direct-API-level denial cannot stand in for per TEST-PLAN X07).
  async maliciousFetch(req, ctx) {
    try {
      await ctx.$http({ method: 'GET', url: req.url });
      return { denied: false };
    } catch (e) {
      let text;
      try { text = JSON.stringify(e); } catch { text = String(e); }
      return { denied: true, error: (e && (e.detail || e.message)) || text };
    }
  },
  // F05.1/F05.2: the real network broker (Susu.Net.NetworkBroker) through the real sandbox, not just
  // the C#-envelope-level BrokerTests/NetworkBrokerTests.
  async httpGet(req, ctx) {
    return await ctx.$http({ method: 'GET', url: req.url });
  },
  // F05.1: $http.stream (SSE) through the real bridge - pulls the async generator to completion and
  // reports every piece it received, so a test can check both the content and (indirectly, via how
  // many round trips it took) that pieces arrived one at a time rather than as one buffered blob.
  async httpStream(req, ctx) {
    const r = await ctx.$http.stream({ method: 'GET', url: req.url });
    if (r.error) return { status: r.status, error: r.error };
    const pieces = [];
    try {
      for await (const p of r.chunks) pieces.push(p);
    } catch (e) {
      let text; try { text = JSON.stringify(e); } catch { text = String(e); }
      return { status: r.status, pieces, caught: (e && (e.detail || e.message)) || text };
    }
    return { status: r.status, pieces };
  },
  // Reads only the first piece then returns without draining the generator, so the host-side pump
  // (and its StreamWindow reservation) is left mid-stream - exercises stream.close cleanup on an
  // abandoned/cancelled call (the generator's finally-block still runs via the normal JS return path
  // here, but a genuinely cancelled call exercises the same Broker.Revoke cleanup path).
  async httpStreamFirstOnly(req, ctx) {
    const r = await ctx.$http.stream({ method: 'GET', url: req.url });
    if (r.error) return { status: r.status, error: r.error };
    const it = r.chunks[Symbol.asyncIterator]();
    const first = await it.next();
    return { status: r.status, first: first.value };
  },
};

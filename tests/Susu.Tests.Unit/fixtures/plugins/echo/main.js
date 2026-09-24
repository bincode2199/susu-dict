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
};

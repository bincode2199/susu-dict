// F04 integration-test fixture: a minimal plugin with no host API calls, used to exercise the
// real AppContainer sandbox + QuickJS engine end to end (HostSession -> ChildHost -> QuickJsRuntime).
export default {
  async translate(req) {
    return { text: `echo:${req.text}` };
  },
  async spin() {
    for (;;) { /* trips the execution budget on purpose */ }
  },
};

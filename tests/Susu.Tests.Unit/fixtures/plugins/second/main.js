// F04 integration-test fixture: a second, independent package in the same plugin-host process, used
// to check that one package's execution-budget rebuild does not affect another package's runtime.
export default {
  async translate(req) {
    return { text: `second:${req.text}` };
  },
};

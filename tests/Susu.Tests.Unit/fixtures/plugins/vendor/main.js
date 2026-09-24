// F05.4 fixture: contract probes for OCR/ASR/TTS/translate-batch/options/vocab through the real
// AppContainer sandbox + QuickJS engine + IPC channel + Susu.Net.NetworkBroker, against local test
// servers standing in for real vendors (no vendor account exists in this environment).
export default {
  // Batch translation: a JSON array request/response, no file handles.
  async translateBatch(req, ctx) {
    const r = await ctx.$http({
      method: 'POST',
      url: req.url,
      headers: { 'content-type': 'application/json' },
      body: { kind: 'json', value: { Items: req.items.map(i => ({ Id: i.id, Text: i.text })) } },
    });
    return { items: r.body.Items.map(i => ({ id: i.Id, text: i.Text })) };
  },

  // Dynamic options list (PLAN 4.5/manifest schema): field/cursor in, items/nextCursor out.
  async optionsFetch(req, ctx) {
    const r = await ctx.$http({ method: 'GET', url: `${req.url}?field=${encodeURIComponent(req.field)}` });
    return { items: r.body.Items.map(i => ({ value: i.Value, label: i.Label })), nextCursor: r.body.NextCursor };
  },

  // Vocab upsert/lookup (PLAN 4.7): operationId/action in, status/remoteId out.
  async vocabUpsert(req, ctx) {
    const r = await ctx.$http({
      method: 'POST',
      url: req.url,
      headers: { 'content-type': 'application/json' },
      body: { kind: 'json', value: { OperationId: req.operationId, Action: req.action, Word: req.word, Lang: req.lang } },
    });
    return { status: r.body.Status, remoteId: r.body.RemoteId };
  },
};

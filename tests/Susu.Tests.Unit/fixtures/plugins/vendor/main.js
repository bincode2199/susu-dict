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

  // OCR (PLAN 4.5.1 B01-shaped): JSON body with an ImageBase64 field the host fills from the input
  // FileHandle - the plugin never sees the image bytes, only the handle's id.
  async ocr(req, ctx) {
    const r = await ctx.$http({
      method: 'POST',
      url: req.url,
      headers: { 'content-type': 'application/json' },
      body: { kind: 'json', value: { ImageBase64: null, LanguageType: req.lang || 'en' } },
      bodyFiles: [{ pointer: '/ImageBase64', file: req.image.id }],
    });
    return { blocks: r.body.TextDetections.map(d => ({ text: d.DetectedText })) };
  },

  // ASR multipart (B02-shaped, OpenAI/whisper style): the audio file goes as a real multipart field,
  // never as a plugin-visible byte array; response is the "segments" output kind.
  async asrMultipart(req, ctx) {
    const r = await ctx.$http({
      method: 'POST',
      url: req.url,
      body: { kind: 'multipart', fields: [
        { name: 'model', text: req.model },
        { name: 'file', filename: 'audio.wav', contentType: 'audio/wav', file: req.audio.id },
      ] },
    });
    return { kind: 'segments', segments: r.body.segments.map(s => ({ start: s.start, end: s.end, text: s.text })) };
  },

  // ASR inlineData (B02-shaped, Gemini style): a JSON body with a Base64 audio field, "text" output kind.
  async asrInline(req, ctx) {
    const r = await ctx.$http({
      method: 'POST',
      url: req.url,
      headers: { 'content-type': 'application/json' },
      body: { kind: 'json', value: { InlineAudio: null, Model: req.model } },
      bodyFiles: [{ pointer: '/InlineAudio', file: req.audio.id }],
    });
    return { kind: 'text', text: r.body.text };
  },

  // TTS JSON (B03-shaped, Tencent-style): a Base64 audio field extracted host-side into a file; the
  // plugin gets the FileHandle, never the encoded bytes.
  async ttsJson(req, ctx) {
    const r = await ctx.$http({
      method: 'POST',
      url: req.url,
      headers: { 'content-type': 'application/json' },
      body: { kind: 'json', value: { Text: req.text, VoiceType: req.voice || 'default' } },
      responseFiles: [{ name: 'audio', pointer: '/Response/Audio', mime: 'audio/mpeg' }],
    });
    return { audio: r.files.audio };
  },

  // TTS raw (B04-shaped): the whole response body is the audio file, not JSON.
  async ttsRaw(req, ctx) {
    const r = await ctx.$http({
      method: 'POST',
      url: req.url,
      headers: { 'content-type': 'application/json' },
      body: { kind: 'json', value: { Text: req.text, VoiceType: req.voice || 'default' } },
      responseType: 'file',
    });
    return { audio: r.body };
  },
};

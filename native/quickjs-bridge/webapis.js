// Standard Web APIs injected into every plugin runtime (PLAN 4.3): crypto and URLSearchParams.
// Source of truth for webapis.inc (tools/gen-webapis.mjs regenerates it; bridge.c includes it).
// Evaluated once per runtime with the two native helpers; nothing here can reach host secrets: the
// natives only produce OS random bytes and SHA digests of bytes the plugin passes in.
(function (native) {
  'use strict';
  function fail(name, message) { const e = new Error(message); e.name = name; return e; }

  // ---- crypto ----
  const INT_TYPES = [Int8Array, Uint8Array, Uint8ClampedArray, Int16Array, Uint16Array, Int32Array, Uint32Array, BigInt64Array, BigUint64Array];
  function isIntegerArray(v) {
    for (const T of INT_TYPES) if (v instanceof T) return true;
    return false;
  }
  function bytesOf(data) {
    if (data instanceof ArrayBuffer) return new Uint8Array(data.slice(0));
    if (ArrayBuffer.isView(data)) return new Uint8Array(data.buffer.slice(data.byteOffset, data.byteOffset + data.byteLength));
    throw new TypeError('data must be an ArrayBuffer or a view');
  }
  const HASHES = { 'SHA-1': 1, 'SHA-256': 2, 'SHA-384': 3, 'SHA-512': 4 };
  function getRandomValues(array) {
    if (!isIntegerArray(array)) throw fail('TypeMismatchError', 'getRandomValues needs an integer typed array');
    if (array.byteLength > 65536) throw fail('QuotaExceededError', 'getRandomValues is limited to 65536 bytes per call');
    if (array.byteLength > 0) new Uint8Array(array.buffer, array.byteOffset, array.byteLength).set(new Uint8Array(native.random(array.byteLength)));
    return array;
  }
  function randomUUID() {
    const b = new Uint8Array(native.random(16));
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    const h = Array.from(b, (x) => (x < 16 ? '0' : '') + x.toString(16)).join('');
    return h.slice(0, 8) + '-' + h.slice(8, 12) + '-' + h.slice(12, 16) + '-' + h.slice(16, 20) + '-' + h.slice(20);
  }
  function digest(algorithm, data) {
    try {
      const name = String(typeof algorithm === 'object' && algorithm !== null ? algorithm.name : algorithm).toUpperCase();
      const id = HASHES[name];
      if (!id) throw fail('NotSupportedError', 'Unsupported digest algorithm');
      return Promise.resolve(native.digest(id, bytesOf(data).buffer));
    } catch (e) { return Promise.reject(e); }
  }
  const subtle = Object.freeze({ digest });
  const crypto = Object.freeze({ getRandomValues, randomUUID, subtle });
  Object.defineProperty(globalThis, 'crypto', { value: crypto, writable: false, configurable: false, enumerable: false });

  // ---- URLSearchParams (WHATWG URL Standard, application/x-www-form-urlencoded) ----
  function utf8(str) {
    const out = [];
    for (let i = 0; i < str.length; i++) {
      let c = str.charCodeAt(i);
      if (c >= 0xd800 && c <= 0xdbff && i + 1 < str.length) {
        const d = str.charCodeAt(i + 1);
        if (d >= 0xdc00 && d <= 0xdfff) { c = 0x10000 + ((c - 0xd800) << 10) + (d - 0xdc00); i++; }
      }
      if (c >= 0xd800 && c <= 0xdfff) c = 0xfffd; // lone surrogate (USVString)
      if (c < 0x80) out.push(c);
      else if (c < 0x800) out.push(0xc0 | (c >> 6), 0x80 | (c & 63));
      else if (c < 0x10000) out.push(0xe0 | (c >> 12), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
      else out.push(0xf0 | (c >> 18), 0x80 | ((c >> 12) & 63), 0x80 | ((c >> 6) & 63), 0x80 | (c & 63));
    }
    return out;
  }
  function fromCodePoint(cp) {
    if (cp < 0x10000) return String.fromCharCode(cp);
    cp -= 0x10000;
    return String.fromCharCode(0xd800 + (cp >> 10), 0xdc00 + (cp & 0x3ff));
  }
  // UTF-8 decode without BOM, invalid sequences become U+FFFD (WHATWG decoder, maximal subparts).
  function decodeUtf8(b) {
    let s = '', need = 0, cp = 0, seen = 0, lo = 0x80, hi = 0xbf;
    for (let i = 0; i < b.length; i++) {
      const x = b[i];
      if (need === 0) {
        if (x < 0x80) s += String.fromCharCode(x);
        else if (x >= 0xc2 && x <= 0xdf) { need = 1; cp = x & 0x1f; }
        else if (x >= 0xe0 && x <= 0xef) { if (x === 0xe0) lo = 0xa0; if (x === 0xed) hi = 0x9f; need = 2; cp = x & 0xf; }
        else if (x >= 0xf0 && x <= 0xf4) { if (x === 0xf0) lo = 0x90; if (x === 0xf4) hi = 0x8f; need = 3; cp = x & 7; }
        else s += '�';
        continue;
      }
      if (x < lo || x > hi) { need = 0; cp = 0; seen = 0; lo = 0x80; hi = 0xbf; s += '�'; i--; continue; }
      lo = 0x80; hi = 0xbf; cp = (cp << 6) | (x & 63); seen++;
      if (seen === need) { s += fromCodePoint(cp); need = 0; cp = 0; seen = 0; }
    }
    if (need !== 0) s += '�';
    return s;
  }
  function hexVal(c) { return c >= 48 && c <= 57 ? c - 48 : c >= 65 && c <= 70 ? c - 55 : c >= 97 && c <= 102 ? c - 87 : -1; }
  function formDecode(str) {
    const bytes = utf8(str.replace(/\+/g, ' '));
    const out = [];
    for (let i = 0; i < bytes.length; i++) {
      if (bytes[i] === 37 && i + 2 < bytes.length && hexVal(bytes[i + 1]) >= 0 && hexVal(bytes[i + 2]) >= 0) {
        out.push(hexVal(bytes[i + 1]) * 16 + hexVal(bytes[i + 2])); i += 2;
      } else out.push(bytes[i]);
    }
    return decodeUtf8(out);
  }
  const HEX = '0123456789ABCDEF';
  function formEncode(str) {
    let s = '';
    for (const b of utf8(str)) {
      if (b === 32) s += '+';
      else if ((b >= 48 && b <= 57) || (b >= 65 && b <= 90) || (b >= 97 && b <= 122) || b === 42 || b === 45 || b === 46 || b === 95) s += String.fromCharCode(b);
      else s += '%' + HEX[b >> 4] + HEX[b & 15];
    }
    return s;
  }
  function usvStr(v) {
    const s = String(v); let out = '';
    for (let i = 0; i < s.length; i++) {
      const c = s.charCodeAt(i);
      if (c >= 0xd800 && c <= 0xdbff && i + 1 < s.length && s.charCodeAt(i + 1) >= 0xdc00 && s.charCodeAt(i + 1) <= 0xdfff) { out += s[i] + s[i + 1]; i++; }
      else if (c >= 0xd800 && c <= 0xdfff) out += '�';
      else out += s[i];
    }
    return out;
  }
  function parse(input) {
    const list = [];
    for (const part of input.split('&')) {
      if (part === '') continue;
      const eq = part.indexOf('=');
      list.push(eq < 0 ? [formDecode(part), ''] : [formDecode(part.slice(0, eq)), formDecode(part.slice(eq + 1))]);
    }
    return list;
  }
  const LIST = new WeakMap();
  const need = (n, min, name) => { if (n < min) throw new TypeError(name + ': ' + min + ' argument' + (min > 1 ? 's' : '') + ' required'); };
  class URLSearchParams {
    constructor(init) {
      LIST.set(this, []);
      const list = LIST.get(this);
      if (init === undefined || init === null) return;
      if (typeof init === 'object' || typeof init === 'function') {
        if (init instanceof URLSearchParams) { for (const p of LIST.get(init)) list.push([p[0], p[1]]); return; }
        if (typeof init[Symbol.iterator] === 'function') {
          for (const pair of init) {
            if (pair === null || typeof pair !== 'object' || typeof pair[Symbol.iterator] !== 'function') throw new TypeError('Each pair must be an iterable [name, value]');
            const items = Array.from(pair);
            if (items.length !== 2) throw new TypeError('Each pair must contain exactly two items');
            list.push([usvStr(items[0]), usvStr(items[1])]);
          }
          return;
        }
        for (const k of Reflect.ownKeys(init)) {
          if (typeof k !== 'string') continue;
          const d = Object.getOwnPropertyDescriptor(init, k);
          if (d && d.enumerable) {
            const name = usvStr(k), value = usvStr(init[k]);
            list.push([name, value]);
          }
        }
        return;
      }
      let s = usvStr(init);
      if (s[0] === '?') s = s.slice(1);
      for (const p of parse(s)) list.push(p);
    }
    get size() { return LIST.get(this).length; }
    append(name, value) { need(arguments.length, 2, 'append'); LIST.get(this).push([usvStr(name), usvStr(value)]); }
    delete(name, value) {
      need(arguments.length, 1, 'delete');
      const n = usvStr(name), list = LIST.get(this);
      const v = value === undefined ? undefined : usvStr(value);
      for (let i = list.length - 1; i >= 0; i--) if (list[i][0] === n && (v === undefined || list[i][1] === v)) list.splice(i, 1);
    }
    get(name) { need(arguments.length, 1, 'get'); const n = usvStr(name); const p = LIST.get(this).find((x) => x[0] === n); return p ? p[1] : null; }
    getAll(name) { need(arguments.length, 1, 'getAll'); const n = usvStr(name); return LIST.get(this).filter((x) => x[0] === n).map((x) => x[1]); }
    has(name, value) {
      need(arguments.length, 1, 'has');
      const n = usvStr(name), v = value === undefined ? undefined : usvStr(value);
      return LIST.get(this).some((x) => x[0] === n && (v === undefined || x[1] === v));
    }
    set(name, value) {
      need(arguments.length, 2, 'set');
      const n = usvStr(name), v = usvStr(value), list = LIST.get(this);
      const first = list.findIndex((x) => x[0] === n);
      if (first < 0) { list.push([n, v]); return; }
      list[first][1] = v;
      for (let i = list.length - 1; i > first; i--) if (list[i][0] === n) list.splice(i, 1);
    }
    sort() {
      const list = LIST.get(this);
      const sorted = list.map((p, i) => [p, i]).sort((a, b) => (a[0][0] < b[0][0] ? -1 : a[0][0] > b[0][0] ? 1 : a[1] - b[1]));
      for (let i = 0; i < sorted.length; i++) list[i] = sorted[i][0];
    }
    forEach(callback, thisArg) {
      need(arguments.length, 1, 'forEach');
      if (typeof callback !== 'function') throw new TypeError('forEach callback must be a function');
      const list = LIST.get(this);
      for (let i = 0; i < list.length; i++) callback.call(thisArg, list[i][1], list[i][0], this);
    }
    toString() { return LIST.get(this).map((p) => formEncode(p[0]) + '=' + formEncode(p[1])).join('&'); }
    *entries() { const list = LIST.get(this); for (let i = 0; i < list.length; i++) yield [list[i][0], list[i][1]]; }
    *keys() { const list = LIST.get(this); for (let i = 0; i < list.length; i++) yield list[i][0]; }
    *values() { const list = LIST.get(this); for (let i = 0; i < list.length; i++) yield list[i][1]; }
    [Symbol.iterator]() { return this.entries(); }
    get [Symbol.toStringTag]() { return 'URLSearchParams'; }
  }
  Object.defineProperty(globalThis, 'URLSearchParams', { value: URLSearchParams, writable: false, configurable: false, enumerable: false });
})

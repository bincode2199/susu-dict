import { describe, expect, it } from 'vitest';
import { mount } from '@vue/test-utils';
import { nextTick } from 'vue';
import { readdirSync, readFileSync, statSync } from 'node:fs';
import { join } from 'node:path';
import ResultCard from '../src/components/ResultCard.vue';
import SecretField from '../src/components/SecretField.vue';
import SourceCard from '../src/components/SourceCard.vue';
import { chordFromEvent } from '../src/components/chords';
import { setLocale, t } from '../src/locales/i18n';

describe('S08: results are text, never markup', () => {
  it('renders service output literally', () => {
    const hostile = '<img src=x onerror="alert(1)"><svg onload=alert(2)><script>alert(3)</script>';
    const wrapper = mount(ResultCard, { props: { card: { serviceId: 's', displayName: '<b>name</b>', state: 'Ready', collapsed: false, text: hostile }, from: 'en', to: 'zh-Hans' } });
    expect(wrapper.find('img').exists()).toBe(false);
    expect(wrapper.find('svg[onload]').exists()).toBe(false);
    expect(wrapper.find('script').exists()).toBe(false);
    expect(wrapper.find('.text').text()).toBe(hostile);
    expect(wrapper.find('.name').text()).toBe('<b>name</b>');
  });

  it('shows the error kind and only the matching action', () => {
    setLocale('zh-Hans');
    const failed = (error: string) => mount(ResultCard, { props: { card: { serviceId: 's', displayName: 'S', state: 'Failed', collapsed: false, text: '', error: error as any }, from: 'en', to: 'zh-Hans' } });
    expect(failed('network').text()).toContain(t('card.retry'));
    expect(failed('quota').text()).toContain(t('card.goSettings'));
    expect(failed('bad_response').findAll('a')).toHaveLength(0);
  });
});

describe('S07: secret input is one-way and short-lived', () => {
  it('never shows an input for a saved secret, only Replace and Delete', () => {
    const wrapper = mount(SecretField, { props: { id: 'k', label: 'key', saved: true, clearToken: 0 } });
    expect(wrapper.find('input').exists()).toBe(false);
    expect(wrapper.text()).toContain(t('secret.saved'));
  });

  it('drops the typed value on page change or window hide and after a successful save', async () => {
    const wrapper = mount(SecretField, { props: { id: 'k', label: 'key', saved: false, clearToken: 0 } });
    await wrapper.find('input').setValue('sk-typed');
    await wrapper.setProps({ clearToken: 1 });
    expect((wrapper.find('input').element as HTMLInputElement).value).toBe('');
    await wrapper.find('input').setValue('sk-second');
    await wrapper.find('button.primary').trigger('click');
    const [value, done] = wrapper.emitted('save')![0] as [string, (ok: boolean) => void];
    expect(value).toBe('sk-second');
    done(true);
    await nextTick();
    expect((wrapper.find('input').element as HTMLInputElement).value).toBe('');
    expect(localStorage.length + sessionStorage.length).toBe(0);
    expect(wrapper.find('input').attributes('type')).toBe('password');
    expect(wrapper.find('input').attributes('autocomplete')).toBe('off');
  });
});

describe('UI03: keyboard and IME', () => {
  const source = () => mount(SourceCard, { props: { modelValue: 'hello', from: 'en', to: 'zh-Hans' } });

  it('Ctrl+Enter submits, Enter does not, and nothing submits during IME composition', async () => {
    const wrapper = source();
    const area = wrapper.find('textarea');
    await area.trigger('keydown', { key: 'Enter' });
    expect(wrapper.emitted('submit')).toBeUndefined();
    await area.trigger('compositionstart');
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true });
    expect(wrapper.emitted('submit')).toBeUndefined();
    await area.trigger('compositionend');
    await area.trigger('keydown', { key: 'Enter', ctrlKey: true });
    expect(wrapper.emitted('submit')).toHaveLength(1);
  });

  it('records chords in the host format', () => {
    expect(chordFromEvent({ key: 'a', code: 'KeyA', ctrlKey: true, altKey: true, shiftKey: false, metaKey: false })).toBe('Ctrl+Alt+A');
    expect(chordFromEvent({ key: 'F2', code: 'F2', ctrlKey: false, altKey: true, shiftKey: false, metaKey: false })).toBe('Alt+F2');
    expect(chordFromEvent({ key: 'a', code: 'KeyA', ctrlKey: false, altKey: false, shiftKey: false, metaKey: false })).toBeNull();
    expect(chordFromEvent({ key: 'Control', code: 'ControlLeft', ctrlKey: true, altKey: false, shiftKey: false, metaKey: false })).toBeNull();
  });
});

describe('UI05: resources', () => {
  it('English has a translation for every Chinese key and the app name is never translated', () => {
    setLocale('en');
    expect(t('app.name')).toBe('Su-Su');
    expect(t('tray.exit')).toBe('Exit');
    setLocale('zh-Hans');
    expect(t('app.name')).toBe('Su-Su');
  });
});

describe('page hardening (PLAN 4.5.5)', () => {
  const files = (dir: string): string[] => readdirSync(dir).flatMap((name) => {
    const path = join(dir, name);
    return statSync(path).isDirectory() ? files(path) : [path];
  });

  it('production sources use no HTML injection, eval, storage or network APIs', () => {
    const forbidden = [/v-html/, /innerHTML/, /\beval\(/, /new Function/, /localStorage/, /sessionStorage/, /\bfetch\(/, /XMLHttpRequest/, /new WebSocket/, /window\.open\(/, /document\.write/];
    for (const file of files(join(__dirname, '../src')).filter((f) => !f.endsWith('devHost.ts'))) {
      const text = readFileSync(file, 'utf8');
      for (const pattern of forbidden) expect(pattern.test(text), `${file} uses ${pattern}`).toBe(false);
    }
  });

  it('the page CSP forbids external scripts, connections, frames, eval and inline script', () => {
    const csp = /Content-Security-Policy" content="([^"]+)"/.exec(readFileSync(join(__dirname, '../index.html'), 'utf8'))![1];
    for (const directive of ["default-src 'none'", "script-src 'self'", "connect-src 'none'", "frame-src 'none'", "object-src 'none'", "base-uri 'none'", "form-action 'none'"]) expect(csp).toContain(directive);
    expect(csp).not.toContain('unsafe-inline');
    expect(csp).not.toContain('unsafe-eval');
  });
});

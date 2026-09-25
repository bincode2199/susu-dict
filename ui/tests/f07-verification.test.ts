// F07 independent verification (CFG02): failures on later pages or refreshes keep what is shown, a load in
// flight is not sent twice, and a page closed mid-load does not fail.
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CommandResult, ConfigFieldView, OptionsView, ServiceView } from '@protocol/ui';
import ConfigForm from '../src/components/ConfigForm.vue';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; });

type Call = { name: string; payload?: any };
function bridgeOf(answer: (payload: any) => CommandResult | Promise<CommandResult>) {
  const calls: Call[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: any) => { calls.push({ name, payload }); return answer(payload); }) };
}
const result = (revision: number, values: string[], extra: Partial<OptionsView> = {}): CommandResult =>
  ({ ok: true, value: { instanceId: 'openai', field: 'model', dependsOnRevision: revision, items: values.map((v) => ({ value: v, label: v })), ...extra } });
const model = (extra: Partial<ConfigFieldView> = {}): ConfigFieldView => ({ name: 'model', type: 'string', default: 'gpt-4o-mini', title: 'Model', dynamic: true, optionsRevision: 1, ...extra });
const openai = (config: ConfigFieldView[]): ServiceView => ({
  serviceId: 'openai/translate', instanceId: 'openai', capability: 'translate', page: 'ai', enabled: true, availability: 'Ready', implemented: true,
  secretNames: ['apiKey'], accountId: 'openai', order: 1, instanceRevision: 4, config,
  credentialTargets: [{ secret: 'apiKey', origin: 'https://api.openai.com:443', use: 'header:Authorization', saved: true, granted: true }],
});
const values = (wrapper: ReturnType<typeof mount>) => wrapper.find('select[data-field="model"]').findAll('option').map((o) => o.attributes('value'));

describe('F07 verification: dynamic options (CFG02)', () => {
  it('a failed next page keeps the loaded list and the saved selection', async () => {
    let answer: (p: any) => CommandResult = () => result(1, ['gpt-a', 'gpt-b'], { nextCursor: 'p2' });
    const bridge = bridgeOf((p) => answer(p));
    const wrapper = mount(ConfigForm, { props: { service: openai([model({ value: 'gpt-b' })]), bridge } });
    await flushPromises();
    expect(values(wrapper)).toEqual(['', 'gpt-a', 'gpt-b']);
    answer = () => result(1, [], { error: 'timeout' });
    await wrapper.find('[data-action="more"]').trigger('click');
    await flushPromises();
    expect(values(wrapper)).toEqual(['', 'gpt-a', 'gpt-b']);
    expect((wrapper.find('select[data-field="model"]').element as HTMLSelectElement).value).toBe('gpt-b');
    expect(wrapper.text()).toContain(t('options.failed', { reason: t('error.timeout') }));
    expect(wrapper.find('[data-action="save-config"]').exists()).toBe(false);
  });

  it('a failed refresh keeps the list; a refused command shows no host detail', async () => {
    let answer: (p: any) => CommandResult = () => result(1, ['gpt-a']);
    const bridge = bridgeOf((p) => answer(p));
    const wrapper = mount(ConfigForm, { props: { service: openai([model({ value: 'gpt-a' })]), bridge } });
    await flushPromises();
    answer = () => ({ ok: false, error: 'unavailable sk-SHOULD-NOT-SHOW' });
    await wrapper.find('[data-action="refresh"]').trigger('click');
    await flushPromises();
    expect(values(wrapper)).toEqual(['', 'gpt-a']);
    expect(wrapper.text()).not.toContain('sk-SHOULD-NOT-SHOW');
    expect(wrapper.text()).toContain(t('options.failed', { reason: t('error.unavailable') }));
  });

  it('a refresh while the list is loading is not sent twice', async () => {
    let release: (r: CommandResult) => void = () => {};
    const bridge = bridgeOf(() => new Promise<CommandResult>((resolve) => { release = resolve; }));
    const wrapper = mount(ConfigForm, { props: { service: openai([model()]), bridge } });
    await flushPromises();
    await wrapper.find('[data-action="refresh"]').trigger('click');
    await flushPromises();
    expect(bridge.calls.filter((c) => c.name === 'Settings.LoadOptions')).toHaveLength(1);
    release(result(1, ['gpt-a']));
    await flushPromises();
    expect(values(wrapper)).toEqual(['', 'gpt-a']);
  });

  it('leaving the page while a list is loading does not fail when the answer arrives', async () => {
    let release: (r: CommandResult) => void = () => {};
    const bridge = bridgeOf(() => new Promise<CommandResult>((resolve) => { release = resolve; }));
    const wrapper = mount(ConfigForm, { props: { service: openai([model()]), bridge } });
    await flushPromises();
    wrapper.unmount();
    release(result(1, ['gpt-a']));
    await flushPromises(); // an unhandled rejection here fails the run
    expect(bridge.calls).toHaveLength(1);
  });
});

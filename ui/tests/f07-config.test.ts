// F07.2: controls generated from the manifest config schema, dynamic options and stale revisions (CFG02).
import { afterEach, describe, expect, it, vi } from 'vitest';
import { flushPromises, mount } from '@vue/test-utils';
import type { CommandResult, ConfigFieldView, OptionsView, ServiceView } from '@protocol/ui';
import ConfigForm from '../src/components/ConfigForm.vue';
import ServiceDetails from '../src/components/ServiceDetails.vue';
import { setLocale, t } from '../src/locales/i18n';

setLocale('zh-Hans');
afterEach(() => { document.body.innerHTML = ''; });

type Call = { name: string; payload?: any };
function fakeBridge(answer: (name: string, payload?: any) => CommandResult | Promise<CommandResult> = () => ({ ok: true })) {
  const calls: Call[] = [];
  return { calls, command: vi.fn(async (name: string, payload?: object) => { calls.push({ name, payload }); return answer(name, payload); }) };
}
const loads = (calls: Call[]) => calls.filter((c) => c.name === 'Settings.LoadOptions').map((c) => c.payload);
const optionsResult = (revision: number, values: string[], extra: Partial<OptionsView> = {}): CommandResult =>
  ({ ok: true, value: { instanceId: 'openai', field: 'model', dependsOnRevision: revision, items: values.map((v) => ({ value: v, label: v })), ...extra } });

const ORIGIN = 'https://api.openai.com:443';
const model = (extra: Partial<ConfigFieldView> = {}): ConfigFieldView => ({ name: 'model', type: 'string', default: 'gpt-4o-mini', title: 'Model', dynamic: true, optionsRevision: 1, ...extra });
const baseUrl: ConfigFieldView = { name: 'baseUrl', type: 'string', format: 'uri', group: 'advanced', title: 'API address', placeholder: 'https://api.openai.com', optionsRevision: 0 };
const openai = (config: ConfigFieldView[], extra: Partial<ServiceView> = {}): ServiceView => ({
  serviceId: 'openai/translate', instanceId: 'openai', capability: 'translate', page: 'ai', enabled: true, availability: 'Ready', implemented: true,
  secretNames: ['apiKey'], accountId: 'openai', order: 1, instanceRevision: 4, config,
  credentialTargets: [{ secret: 'apiKey', origin: ORIGIN, use: 'header:Authorization', saved: true, granted: true }], ...extra,
});

describe('F07.2 generated controls', () => {
  it('renders one control per schema field by type, with the advanced group and showWhen', async () => {
    const fields: ConfigFieldView[] = [
      model({ dynamic: false, optionsRevision: 0 }),
      { name: 'formality', type: 'string', enum: ['default', 'more', 'less'], optionsRevision: 0 },
      { name: 'temperature', type: 'number', minimum: 0, maximum: 2, default: '0.3', optionsRevision: 0 },
      { name: 'stream', type: 'boolean', default: 'false', optionsRevision: 0 },
      { name: 'chunk', type: 'integer', showWhenField: 'stream', showWhenEquals: 'true', optionsRevision: 0 },
      baseUrl,
    ];
    const wrapper = mount(ConfigForm, { props: { service: openai(fields), bridge: fakeBridge() } });
    expect(wrapper.find('input[data-field="model"]').attributes('type')).toBe('text'); // no options method: a free field
    expect(wrapper.findAll('select[data-field="formality"] option').map((o) => o.attributes('value'))).toEqual(['', 'default', 'more', 'less']);
    const temperature = wrapper.find('input[data-field="temperature"]');
    expect([temperature.attributes('type'), temperature.attributes('min'), temperature.attributes('max')]).toEqual(['number', '0', '2']);
    expect(wrapper.find('input[data-field="chunk"]').exists()).toBe(false); // shown only when stream is on
    await wrapper.find('button[role="switch"]').trigger('click');
    expect(wrapper.find('input[data-field="chunk"]').exists()).toBe(true);
    expect(wrapper.find('.group').text()).toBe(t('config.advanced'));
    const url = wrapper.find('input[data-field="baseUrl"]');
    expect([url.attributes('type'), url.attributes('placeholder')]).toEqual(['url', 'https://api.openai.com']);
    expect(wrapper.text()).toContain(t('config.title.baseUrl')); // localized title wins over the manifest's
  });

  it('saves only changed values with the instance revision; the host verdict is shown', async () => {
    const bridge = fakeBridge((name) => name === 'Settings.SaveServiceConfig'
      ? { ok: false, error: 'invalid', value: [{ path: 'instances.openai.config.baseUrl', code: 'uri', message: '', line: 0 }] }
      : { ok: true });
    const wrapper = mount(ConfigForm, { props: { service: openai([model({ dynamic: false, optionsRevision: 0, value: 'gpt-b' }), baseUrl]), bridge } });
    expect(wrapper.find('[data-action="save-config"]').exists()).toBe(false);
    await wrapper.find('input[data-field="baseUrl"]').setValue('http://llm.example.com');
    await wrapper.find('[data-action="save-config"]').trigger('click');
    await flushPromises();
    expect(bridge.calls).toEqual([{ name: 'Settings.SaveServiceConfig', payload: { instanceId: 'openai', expectedInstanceRevision: 4, values: [{ name: 'baseUrl', value: 'http://llm.example.com' }] } }]);
    expect(wrapper.text()).toContain(t('config.invalid', { field: t('config.title.baseUrl') }));
  });

  it('appears inside the service details of a wired service', () => {
    const wrapper = mount(ServiceDetails, { props: { service: openai([model({ dynamic: false, optionsRevision: 0 })]), accounts: [], bridge: fakeBridge(), clearToken: 0 } });
    expect(wrapper.find('input[data-field="model"]').exists()).toBe(true);
  });
});

describe('CFG02 dynamic options', () => {
  it('loads the list for the current revision and keeps a saved value the list does not contain', async () => {
    const bridge = fakeBridge(() => optionsResult(1, ['gpt-a', 'gpt-b'], { nextCursor: '200' }));
    const wrapper = mount(ConfigForm, { props: { service: openai([model({ value: 'my-finetune' }), baseUrl]), bridge } });
    await flushPromises();
    expect(loads(bridge.calls)).toEqual([{ instanceId: 'openai', field: 'model', dependsOnRevision: 1, cursor: undefined, refresh: false }]);
    const select = wrapper.find('select[data-field="model"]');
    expect(select.findAll('option').map((o) => o.attributes('value'))).toEqual(['', 'my-finetune', 'gpt-a', 'gpt-b']);
    expect((select.element as HTMLSelectElement).value).toBe('my-finetune');
    // Paging appends the next page for the same revision.
    bridge.command.mockImplementation(async (name: string, payload?: any) => { bridge.calls.push({ name, payload }); return optionsResult(1, ['gpt-c']); });
    await wrapper.find('[data-action="more"]').trigger('click');
    await flushPromises();
    expect(loads(bridge.calls)[1]).toMatchObject({ dependsOnRevision: 1, cursor: '200' });
    expect(select.findAll('option').map((o) => o.attributes('value'))).toEqual(['', 'my-finetune', 'gpt-a', 'gpt-b', 'gpt-c']);
    expect(wrapper.find('[data-action="more"]').exists()).toBe(false);
  });

  it('an answer for an old revision never writes back after the account or address changed', async () => {
    const pending: Record<number, (r: CommandResult) => void> = {};
    const bridge = fakeBridge((_name, payload) => new Promise<CommandResult>((resolve) => { pending[payload.dependsOnRevision] = resolve; }));
    const wrapper = mount(ConfigForm, { props: { service: openai([model(), baseUrl]), bridge } });
    await flushPromises();
    expect(Object.keys(pending)).toEqual(['1']);
    await wrapper.setProps({ service: openai([model({ optionsRevision: 2 }), { ...baseUrl, value: 'https://llm.example.com' }], { instanceRevision: 5 }) });
    await flushPromises();
    expect(loads(bridge.calls).map((p) => p.dependsOnRevision)).toEqual([1, 2]);
    pending[1](optionsResult(1, ['model-of-old-account']));
    await flushPromises();
    const values = () => wrapper.find('select[data-field="model"]').findAll('option').map((o) => o.attributes('value'));
    expect(values()).toEqual(['']);
    // The host's own stale verdict is dropped the same way.
    pending[2](optionsResult(3, [], { stale: true }));
    await flushPromises();
    expect(values()).toEqual(['']);
    await wrapper.setProps({ service: openai([model({ optionsRevision: 3 }), baseUrl], { instanceRevision: 6 }) });
    await flushPromises();
    pending[3](optionsResult(3, ['model-of-new-account']));
    await flushPromises();
    expect(values()).toEqual(['', 'model-of-new-account']);
  });

  it('a failed load keeps the current selection and shows only the error kind', async () => {
    const bridge = fakeBridge(() => optionsResult(1, [], { error: 'auth' }));
    const wrapper = mount(ConfigForm, { props: { service: openai([model({ value: 'gpt-chosen' })]), bridge } });
    await flushPromises();
    expect((wrapper.find('select[data-field="model"]').element as HTMLSelectElement).value).toBe('gpt-chosen');
    expect(wrapper.text()).toContain(t('options.failed', { reason: t('error.auth') }));
    expect(wrapper.find('[data-action="save-config"]').exists()).toBe(false); // nothing was changed on the user's behalf
  });

  it('without a saved and authorized key nothing is requested until the key is granted', async () => {
    const bridge = fakeBridge(() => optionsResult(1, ['gpt-a']));
    const unsaved = [{ secret: 'apiKey', origin: ORIGIN, use: 'header:Authorization', saved: true, granted: false }];
    const wrapper = mount(ConfigForm, { props: { service: openai([model()], { credentialTargets: unsaved }), bridge } });
    await flushPromises();
    expect(bridge.calls).toEqual([]);
    expect(wrapper.text()).toContain(t('options.needsCredential'));
    await wrapper.setProps({ service: openai([model()]) });
    await flushPromises();
    expect(loads(bridge.calls)).toHaveLength(1);
    expect(wrapper.find('select[data-field="model"]').findAll('option').map((o) => o.attributes('value'))).toEqual(['', 'gpt-a']);
  });

  it('refresh asks the host to bypass its cache', async () => {
    const bridge = fakeBridge(() => optionsResult(1, ['gpt-a'], { cached: true }));
    const wrapper = mount(ConfigForm, { props: { service: openai([model()]), bridge } });
    await flushPromises();
    await wrapper.find('[data-action="refresh"]').trigger('click');
    await flushPromises();
    expect(loads(bridge.calls).map((p) => p.refresh)).toEqual([false, true]);
  });
});

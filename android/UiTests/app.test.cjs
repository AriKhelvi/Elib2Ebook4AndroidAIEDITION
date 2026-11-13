// Регрессии темы и взаимодействия с клавиатурой без Android/DOM-зависимостей.
// Запуск: node --test android/UiTests/*.test.cjs
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');

const source = fs.readFileSync(path.join(__dirname, '../E2E.Droid/wwwroot/js/app.js'), 'utf8');
const initialPalette = {
    light: { '--md-primary': '#6750A4', '--md-surface': '#FFFBFE' },
    dark: { '--md-primary': '#D0BCFF', '--md-surface': '#1C1B1F' }
};

function app({ dark = false, mode = null, palette = null, dynamic = true } = {}) {
    const stored = new Map();
    if (mode) stored.set('elib-theme', mode);
    if (palette) stored.set('elib-palette', JSON.stringify(palette));
    if (!dynamic) stored.set('elib-dynamic', '0');
    const properties = new Map();
    const attributes = new Map();
    const classes = new Set();
    const events = new Map();
    const nativeCalls = [];
    let systemChange;
    const media = { matches: dark, addEventListener: (name, listener) => { systemChange = listener; } };
    const document = {
        visibilityState: 'visible',
        activeElement: null,
        documentElement: {
            setAttribute: (name, value) => attributes.set(name, value),
            getAttribute: name => attributes.get(name),
            style: { setProperty: (key, value) => properties.set(key, value), removeProperty: key => properties.delete(key) }
        },
        body: { classList: { add: value => classes.add(value), remove: value => classes.delete(value) } },
        addEventListener: (name, listener) => events.set(name, listener)
    };
    const window = {
        matchMedia: () => media,
        DotNet: { invokeMethodAsync: (assembly, method, ...args) => {
            nativeCalls.push({ method, args });
            return Promise.resolve(method === 'ReadDynamicPalette' ? window.nextPalette : undefined);
        } },
        nextPalette: palette
    };
    vm.runInNewContext(source, {
        window, document,
        localStorage: { getItem: key => stored.get(key) ?? null, setItem: (key, value) => stored.set(key, value), removeItem: key => stored.delete(key) },
        getComputedStyle: () => ({ getPropertyValue: key => properties.get(key) || '#F8F9FF' }),
        setTimeout: callback => callback(),
        requestAnimationFrame: callback => callback()
    });
    return {
        window, document, stored, properties, classes, nativeCalls,
        theme: () => attributes.get('data-theme'),
        dispatch: (name, event) => events.get(name)?.(event),
        systemTheme: value => { media.matches = value; systemChange(); },
        focus: (type, extra = {}) => {
            document.activeElement = { tagName: 'INPUT', type, ...extra };
            events.get('focusin')({ target: document.activeElement });
        },
        blur: () => { document.activeElement = null; events.get('focusout')(); }
    };
}

test('системная тема следует системе, явный выбор сохраняется', () => {
    const state = app();
    assert.equal(state.theme(), 'light');
    state.systemTheme(true);
    assert.equal(state.theme(), 'dark');
    state.window.e2e.setThemeMode('light');
    state.systemTheme(false);
    state.systemTheme(true);
    assert.equal(state.theme(), 'light');
    assert.equal(state.stored.get('elib-theme'), 'light');
});

test('динамическая палитра меняет обе темы и очищается при отключении', () => {
    const state = app({ palette: initialPalette });
    assert.equal(state.properties.get('--md-primary'), '#6750A4');
    state.window.e2e.setThemeMode('dark');
    assert.equal(state.properties.get('--md-primary'), '#D0BCFF');
    state.window.e2e.setDynamicEnabled(false);
    assert.equal(state.properties.size, 0);
    state.window.e2e.setDynamicEnabled(true);
    assert.equal(state.properties.get('--md-primary'), '#D0BCFF');
});

test('AMOLED сохраняет чёрные поверхности, e-ink не получает цвета из обоев', () => {
    const state = app({ palette: initialPalette });
    state.window.e2e.setThemeMode('amoled');
    assert.equal(state.properties.get('--md-primary'), '#D0BCFF');
    assert.equal(state.properties.has('--md-surface'), false);
    state.window.e2e.setThemeMode('eink');
    assert.equal(state.properties.size, 0);
});

test('без динамической палитры используются CSS-цвета, включая Android до12', () => {
    const state = app({ palette: initialPalette });
    state.window.e2e.setDynamicPalette(null);
    assert.equal(state.properties.size, 0);
    assert.equal(state.stored.has('elib-palette'), false);
});

test('range, switch, кнопки и readonly-поля не скрывают навигацию как клавиатура', () => {
    const state = app();
    for (const type of ['range', 'checkbox', 'radio', 'button', 'file', 'color']) {
        state.focus(type);
        assert.equal(state.classes.has('kb-open'), false, type);
        state.blur();
    }
    state.focus('text', { readOnly: true });
    assert.equal(state.classes.has('kb-open'), false);
    state.focus('number', { disabled: true });
    assert.equal(state.classes.has('kb-open'), false);
});

test('редактируемые текстовые поля скрывают навигацию до выхода из поля', () => {
    const state = app();
    for (const type of ['text', 'number', 'email', 'url', 'tel', 'password', 'search']) {
        state.focus(type);
        assert.equal(state.classes.has('kb-open'), true, type);
        state.blur();
        assert.equal(state.classes.has('kb-open'), false, type);
    }
});

test('возврат из фона обновляет обои без изменения выбранной темы', async () => {
    const state = app({ palette: initialPalette, mode: 'dark' });
    state.window.nextPalette = { light: {}, dark: { '--md-primary': '#B1D18A', '--md-surface': '#11140E' } };
    state.document.visibilityState = 'hidden';
    state.dispatch('visibilitychange');
    assert.equal(state.nativeCalls.length, 0);
    state.document.visibilityState = 'visible';
    state.dispatch('visibilitychange');
    state.dispatch('visibilitychange');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(state.nativeCalls.filter(call => call.method === 'ReadDynamicPalette').length, 1);
    assert.equal(state.properties.get('--md-primary'), '#B1D18A');
    assert.equal(state.theme(), 'dark');
});

test('ошибка нативного чтения сохраняет палитру и не блокирует следующую попытку', async () => {
    const state = app({ palette: initialPalette });
    state.window.DotNet.invokeMethodAsync = () => Promise.reject(new Error('bridge not ready'));
    state.dispatch('visibilitychange');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(state.properties.get('--md-primary'), '#6750A4');
    state.window.DotNet.invokeMethodAsync = () => Promise.resolve(null);
    state.dispatch('visibilitychange');
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(state.properties.size, 0);
});

// Изолированные проверки настоящего модуля без загрузки Яндекса и без дополнительных npm-пакетов.
import test from "node:test";
import assert from "node:assert/strict";
import { readFile } from "node:fs/promises";
import vm from "node:vm";

const source = await readFile(new URL("../wwwroot/js/metrika.js", import.meta.url), "utf8");
const origin = "https://equipment-manager-demo.onrender.com";
const id = 113657931;

function browser(path = "/equipment", autoLoad = true) {
    const calls = [];
    const scripts = [];
    const warnings = [];
    const window = { location: new URL(origin + path) };
    const timers = [];
    const document = {
        get title() { throw new Error("Нельзя читать пользовательский заголовок"); },
        get referrer() { throw new Error("Нельзя читать исходный Referer"); },
        get forms() { throw new Error("Нельзя читать формы"); },
        createElement(name) {
            assert.equal(name, "script");
            return { remove() { this.removed = true; } };
        },
        head: {
            appendChild(script) {
                scripts.push(script);
                if (autoLoad) queueMicrotask(() => load(script));
            }
        }
    };
    function load(script) {
        window.ym = (...args) => calls.push(JSON.parse(JSON.stringify(args)));
        script.onload?.();
    }
    const context = {
        window, document,
        console: { warn(message) { warnings.push(message); } },
        setTimeout(callback) { timers.push(callback); return timers.length; },
        clearTimeout() { }
    };
    const module = vm.runInNewContext(`(function () { ${source.replace(/^export /gm, "")}\n return { track, pause }; })()`, context);
    return { module, calls, scripts, warnings, window, timers, load,
        navigate(path) { window.location = new URL(origin + path); },
        hits() { return calls.filter(call => call[1] === "hit"); } };
}

test("Инициализация не считает просмотр; явный hit один, поля безопасны", async () => {
    const b = browser();
    await b.module.track(id, origin, "/equipment");
    assert.equal(b.scripts.length, 1);
    assert.equal(b.scripts[0].src, "https://mc.yandex.ru/metrika/tag.js");
    assert.equal(b.scripts[0].referrerPolicy, "no-referrer");
    const init = b.calls.find(call => call[1] === "init")[2];
    assert.equal(init.defer, true);
    for (const option of ["webvisor", "clickmap", "trackLinks", "trackHash", "accurateTrackBounce", "ecommerce", "childIframe", "sendTitle"])
        assert.equal(init[option], false, option);
    assert.equal(init.disableYtm, true);
    assert.equal(init.url, origin + "/equipment");
    assert.equal(init.referrer, origin + "/");
    assert.equal(b.hits().length, 1);
    assert.deepEqual(b.hits()[0], [id, "hit", origin + "/equipment", { title: "Оборудование", referer: origin + "/" }]);
    assert.deepEqual(Object.keys(init).filter(key => /params|userid/i.test(key)), []);
});

test("Ререндер и гидратация не дублируют просмотры; переходы и Back считаются", async () => {
    const b = browser();
    await b.module.track(id, origin, "/equipment");
    await b.module.track(id, origin, "/equipment");
    b.navigate("/faults");
    await b.module.track(id, origin, "/faults");
    b.navigate("/equipment");
    await b.module.track(id, origin, "/equipment");
    assert.equal(b.calls.filter(call => call[1] === "init").length, 1);
    assert.equal(b.scripts.length, 1);
    assert.deepEqual(b.hits().map(call => call[2]), [origin + "/equipment", origin + "/faults", origin + "/equipment"]);
    assert.equal(b.hits()[1][3].referer, origin + "/equipment");
});

test("Login, logout, сеанс, OAuth и неизвестные пути не загружают счётчик", async () => {
    for (const path of ["/login", "/account/login", "/account/logout", "/account/session", "/account/oauth/callback?code=secret&state=secret", "/oauth/yandex/userinfo", "/api/equipment", "/equipment/private@example.invalid", "/Error"]) {
        const b = browser(path);
        await b.module.track(id, origin, path);
        assert.equal(b.scripts.length, 0, path);
        assert.equal(b.calls.length, 0, path);
    }
});

test("Query/hash с email, JWT и UUID не передаются даже с разрешённым pathname", async () => {
    for (const path of ["/equipment?email=private@example.invalid", "/equipment#access_token=synthetic-jwt", "/equipment?user=11111111-1111-1111-1111-111111111111"]) {
        const b = browser(path);
        await b.module.track(id, origin, "/equipment");
        assert.equal(b.scripts.length, 0);
        assert.equal(b.calls.length, 0);
    }
});

test("Неверный origin, HTTP и некорректный ID отключают отправку", async () => {
    for (const counterId of [0, -1, "113657931", Number.MAX_SAFE_INTEGER + 1]) {
        const b = browser();
        await b.module.track(counterId, origin, "/equipment");
        assert.equal(b.scripts.length, 0);
    }
    const b = browser();
    await b.module.track(id, "https://foreign.invalid", "/equipment");
    b.window.location = new URL("http://equipment-manager-demo.onrender.com/equipment");
    await b.module.track(id, origin, "/equipment");
    assert.equal(b.scripts.length, 0);
});

test("Устаревший маршрут не отправляется после асинхронной загрузки", async () => {
    const b = browser("/equipment", false);
    const pending = b.module.track(id, origin, "/equipment");
    b.navigate("/account/oauth/callback?code=synthetic");
    b.module.pause();
    b.load(b.scripts[0]);
    await pending;
    assert.equal(b.calls.length, 0);
});

test("При конкурирующих переходах считается только актуальный маршрут", async () => {
    const b = browser("/equipment", false);
    const first = b.module.track(id, origin, "/equipment");
    b.navigate("/faults");
    const second = b.module.track(id, origin, "/faults");
    b.load(b.scripts[0]);
    await Promise.all([first, second]);
    assert.deepEqual(b.hits().map(call => call[2]), [origin + "/faults"]);
});

test("Pause разрушает счётчик; возврат на безопасный маршрут создаёт новый просмотр", async () => {
    const b = browser();
    await b.module.track(id, origin, "/equipment");
    b.navigate("/account/session");
    b.module.pause();
    assert.equal(b.calls.filter(call => call[1] === "destruct").length, 1);
    b.navigate("/equipment");
    await b.module.track(id, origin, "/equipment");
    assert.equal(b.hits().length, 2);
    assert.equal(b.scripts.length, 1);
});

test("Ошибка загрузки или таймаут не мешают приложению и не отправляют просмотры", async () => {
    for (const failure of ["network", "timeout"]) {
        const b = browser("/equipment", false);
        const pending = b.module.track(id, origin, "/equipment");
        if (failure === "network") b.scripts[0].onerror();
        else b.timers[0]();
        await pending;
        assert.equal(b.hits().length, 0);
        assert.equal(b.warnings.length, 1);
        assert.equal(b.scripts[0].removed, true);
    }
});

test("Исключение сторонней библиотеки не содержит секретов в диагностике", async () => {
    const b = browser("/equipment", false);
    const pending = b.module.track(id, origin, "/equipment");
    b.load(b.scripts[0]);
    b.window.ym = () => { throw new Error("private-email@example.invalid synthetic-jwt"); };
    await pending;
    assert.deepEqual(b.warnings, ["Не удалось отправить просмотр Метрики."]);
});

// Только публичные названия маршрутов: document.title, DOM, формы и данные сеанса не читаются.
const titles = new Map([
    ["/", "Главная"],
    ["/equipment", "Оборудование"],
    ["/faults", "Неисправности"],
    ["/maintenance-history", "История обслуживания"],
    ["/counter", "Счётчик"],
    ["/weather", "Погода"]
]);

let loading;
let activeId;
let lastPage;
let generation = 0;

function safePage(origin, path) {
    if (!titles.has(path)) return false;
    const current = window.location;
    return current.protocol === "https:" && current.origin === origin
        && current.pathname === path && !current.search && !current.hash;
}

function loadTag() {
    if (loading) return loading;
    loading = new Promise(resolve => {
        // Официальная очередь: до загрузки библиотеки в неё не помещаются init или hit.
        window.ym = window.ym || function () { (window.ym.a = window.ym.a || []).push(arguments); };
        window.ym.l = window.ym.l || Date.now();
        const script = document.createElement("script");
        script.async = true;
        script.src = "https://mc.yandex.ru/metrika/tag.js";
        script.referrerPolicy = "no-referrer";
        const timer = setTimeout(() => finish(false), 15000);
        function finish(success) {
            clearTimeout(timer);
            script.onload = null;
            script.onerror = null;
            if (!success) {
                script.remove();
                console.warn("Метрика недоступна; приложение продолжает работу.");
            }
            resolve(success);
        }
        script.onload = () => finish(true);
        script.onerror = () => finish(false);
        document.head.appendChild(script);
    });
    return loading;
}

// Прекращает работу и сбрасывает только аналитику; cookie авторизации и SignalR не затрагиваются.
export function pause() {
    generation++;
    if (activeId !== undefined) {
        try { window.ym(activeId, "destruct"); }
        catch { console.warn("Не удалось остановить Метрику."); }
    }
    activeId = undefined;
    lastPage = undefined;
}

export async function track(counterId, origin, path) {
    const requestGeneration = ++generation;
    if (!Number.isSafeInteger(counterId) || counterId <= 0 || !safePage(origin, path)) {
        pause();
        return;
    }
    if (!await loadTag() || requestGeneration !== generation || !safePage(origin, path)) return;
    if (activeId !== undefined && activeId !== counterId) pause();
    const url = origin + path;
    try {
        if (activeId === undefined) {
            window.ym(counterId, "init", {
                defer: true,
                url,
                referrer: origin + "/",
                webvisor: false,
                clickmap: false,
                trackLinks: false,
                trackHash: false,
                accurateTrackBounce: false,
                ecommerce: false,
                childIframe: false,
                sendTitle: false,
                disableYtm: true
            });
            activeId = counterId;
        }
        // Повторный рендер, гидратация и query/hash не создают второй просмотр.
        if (lastPage === url) return;
        window.ym(counterId, "hit", url, { title: titles.get(path), referer: lastPage || origin + "/" });
        lastPage = url;
    } catch {
        console.warn("Не удалось отправить просмотр Метрики.");
    }
}

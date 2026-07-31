"use strict";

const assert = require("node:assert/strict");
const fs = require("node:fs");
const vm = require("node:vm");

const productionScriptPath = process.argv[2];
if (!productionScriptPath) {
    throw new Error("Expected the production secret-reveal.js path.");
}
const productionScript = fs.readFileSync(productionScriptPath, "utf8");

class FakeClock {
    #nextId = 1;
    #now = 0;
    #tasks = new Map();

    setTimeout(callback, delay) {
        return this.#schedule(callback, delay, false);
    }

    setInterval(callback, delay) {
        return this.#schedule(callback, delay, true);
    }

    clear(id) {
        this.#tasks.delete(id);
    }

    tick(milliseconds) {
        const target = this.#now + milliseconds;
        while (true) {
            const next = [...this.#tasks.values()]
                .filter(task => task.due <= target)
                .sort((left, right) => left.due - right.due || left.id - right.id)[0];
            if (!next) {
                break;
            }

            this.#now = next.due;
            if (next.repeats) {
                next.due += next.delay;
            } else {
                this.#tasks.delete(next.id);
            }
            next.callback();
        }
        this.#now = target;
    }

    #schedule(callback, delay, repeats) {
        const id = this.#nextId++;
        this.#tasks.set(id, {
            id,
            callback,
            delay,
            due: this.#now + delay,
            repeats,
        });
        return id;
    }
}

class FakeElement {
    constructor(kind, textContent = "") {
        this.kind = kind;
        this.textContent = textContent;
        this.hidden = false;
        this.dataset = {};
        this.attributes = {};
        this.parentElement = null;
        this.queries = new Map();
    }

    querySelector(selector) {
        return this.queries.get(selector) ?? null;
    }

    closest(selector) {
        let current = this;
        while (current) {
            if (current.#matches(selector)) {
                return current;
            }
            current = current.parentElement;
        }
        return null;
    }

    #matches(selector) {
        return (selector === "[data-secret-reveal]" && this.kind === "reveal") ||
            (selector === "[data-secret-copy]" && this.kind === "copy") ||
            (selector === "[data-secret-panel]" && this.kind === "panel");
    }
}

class FakeEventTarget {
    constructor() {
        this.listeners = new Map();
    }

    addEventListener(type, callback) {
        const listeners = this.listeners.get(type) ?? [];
        listeners.push(callback);
        this.listeners.set(type, listeners);
    }

    dispatch(type, event = {}) {
        for (const callback of this.listeners.get(type) ?? []) {
            callback(event);
        }
    }
}

function createPanel(id, assetId) {
    const panel = new FakeElement("panel");
    panel.hidden = true;
    panel.attributes.id = id;
    const status = new FakeElement(
        "status",
        "服务器密码将在 30 秒后自动隐藏");
    const value = new FakeElement("value");
    const countdown = new FakeElement("countdown", "30");
    const copy = new FakeElement("copy", "复制密码");
    copy.parentElement = panel;
    const reveal = new FakeElement("reveal", "查看密码");
    reveal.dataset.output = id;
    reveal.dataset.assetId = assetId;

    panel.queries.set("[data-secret-status]", status);
    panel.queries.set("[data-secret-value]", value);
    panel.queries.set("[data-secret-countdown]", countdown);
    return {
        panel,
        status,
        value,
        countdown,
        copy,
        reveal,
        nodes: [panel, status, value, countdown, copy, reveal],
    };
}

function makeResponse(status, password) {
    return {
        status,
        ok: status >= 200 && status < 300,
        async json() {
            return { password };
        },
    };
}

function deferredResponse() {
    let resolve;
    const promise = new Promise(resolvePromise => {
        resolve = resolvePromise;
    });
    return {
        promise,
        resolve(status, password) {
            resolve(makeResponse(status, password));
        },
    };
}

function createEnvironment() {
    const clock = new FakeClock();
    const documentTarget = new FakeEventTarget();
    const windowTarget = new FakeEventTarget();
    const primary = createPanel("secret-primary", "asset-primary");
    const secondary = createPanel("secret-secondary", "asset-secondary");
    const panels = [primary.panel, secondary.panel];
    const fetchQueue = [];
    const fetchCalls = [];
    const clipboardWrites = [];
    const redirects = [];
    const tokenInput = { value: "anti-forgery-token" };

    const document = {
        visibilityState: "visible",
        addEventListener: documentTarget.addEventListener.bind(documentTarget),
        getElementById(id) {
            return panels.find(panel => panel.attributes.id === id) ?? null;
        },
        querySelector(selector) {
            if (selector === 'input[name="__RequestVerificationToken"]') {
                return tokenInput;
            }
            return selector === "[data-secret-value]"
                ? primary.value
                : null;
        },
        querySelectorAll(selector) {
            return selector === "[data-secret-panel]" ? panels : [];
        },
    };
    const window = {
        addEventListener: windowTarget.addEventListener.bind(windowTarget),
        clearInterval: clock.clear.bind(clock),
        clearTimeout: clock.clear.bind(clock),
        location: {
            assign(url) {
                redirects.push(url);
            },
        },
        setInterval: clock.setInterval.bind(clock),
        setTimeout: clock.setTimeout.bind(clock),
    };
    async function fetch(url, options) {
        fetchCalls.push({ url, options });
        assert.notEqual(fetchQueue.length, 0, "Unexpected fetch call");
        const next = fetchQueue.shift();
        return await next;
    }
    const navigator = {
        clipboard: {
            async writeText(value) {
                clipboardWrites.push(value);
            },
        },
    };

    vm.runInContext(
        productionScript,
        vm.createContext({
            AbortController,
            Element: FakeElement,
            Map,
            Math,
            String,
            URLSearchParams,
            document,
            fetch,
            navigator,
            window,
        }),
        { filename: productionScriptPath });

    return {
        primary,
        secondary,
        allNodes: [...primary.nodes, ...secondary.nodes],
        clipboardWrites,
        clock,
        fetchCalls,
        queueResponse(status, password) {
            fetchQueue.push(Promise.resolve(makeResponse(status, password)));
        },
        queueDeferred(response) {
            fetchQueue.push(response.promise);
        },
        redirects,
        click(element) {
            documentTarget.dispatch("click", { target: element });
        },
        hideDocument() {
            document.visibilityState = "hidden";
            documentTarget.dispatch("visibilitychange");
        },
        pagehide() {
            windowTarget.dispatch("pagehide");
        },
    };
}

async function flushAsyncWork() {
    for (let turn = 0; turn < 4; turn += 1) {
        await Promise.resolve();
    }
    await new Promise(resolve => setImmediate(resolve));
}

async function reveal(env, password) {
    env.queueResponse(200, password);
    env.click(env.primary.reveal);
    await flushAsyncWork();
}

async function testRequestContractAndPlaintextDestination() {
    const env = createEnvironment();
    const plaintext = "unique-server-password";

    await reveal(env, plaintext);

    assert.equal(env.fetchCalls.length, 1);
    const request = env.fetchCalls[0];
    assert.equal(request.url, "/secrets/reveal?assetId=asset-primary");
    assert.equal(request.options.method, "POST");
    assert.equal(request.options.credentials, "same-origin");
    assert.equal(
        request.options.headers["Content-Type"],
        "application/x-www-form-urlencoded;charset=UTF-8");
    assert.equal(
        request.options.body.get("__RequestVerificationToken"),
        "anti-forgery-token");
    assert.equal(env.primary.value.textContent, plaintext);
    assert.equal(env.primary.panel.hidden, false);
    assert.equal(env.redirects.length, 0);
    assert.equal(request.url.includes(plaintext), false);

    for (const node of env.allNodes) {
        if (node !== env.primary.value) {
            assert.equal(
                node.textContent.includes(plaintext),
                false,
                `${node.kind} received plaintext`);
        }
        assert.equal(JSON.stringify(node.dataset).includes(plaintext), false);
        assert.equal(JSON.stringify(node.attributes).includes(plaintext), false);
    }
}

async function testThirtySecondExpiry() {
    const env = createEnvironment();
    await reveal(env, "expires-at-thirty-seconds");

    env.clock.tick(1_000);
    assert.equal(env.primary.countdown.textContent, "29");
    env.clock.tick(28_999);
    assert.equal(env.primary.value.textContent, "expires-at-thirty-seconds");
    assert.equal(env.primary.panel.hidden, false);
    env.clock.tick(1);
    assert.equal(env.primary.value.textContent, "");
    assert.equal(env.primary.panel.hidden, true);
    assert.equal(env.primary.countdown.textContent, "30");
}

async function testRepeatedRevealClearsAndRejectsLateResponse() {
    const env = createEnvironment();
    await reveal(env, "first-password");
    env.secondary.value.textContent = "other-visible-password";
    env.secondary.panel.hidden = false;
    const second = deferredResponse();
    env.queueDeferred(second);
    env.click(env.primary.reveal);
    assert.equal(env.primary.value.textContent, "");
    assert.equal(env.primary.panel.hidden, true);
    assert.equal(env.secondary.value.textContent, "");
    assert.equal(env.secondary.panel.hidden, true);

    env.queueResponse(200, "third-password");
    env.click(env.primary.reveal);
    await flushAsyncWork();
    assert.equal(env.primary.value.textContent, "third-password");

    second.resolve(200, "late-second-password");
    await flushAsyncWork();
    assert.equal(env.primary.value.textContent, "third-password");
    assert.equal(env.primary.panel.hidden, false);
}

async function testHiddenClearsAndRejectsLateResponse() {
    const env = createEnvironment();
    await reveal(env, "visible-before-hidden");
    const pending = deferredResponse();
    env.queueDeferred(pending);
    env.click(env.primary.reveal);
    env.secondary.value.textContent = "other-hidden-password";
    env.secondary.panel.hidden = false;

    env.hideDocument();
    pending.resolve(200, "late-hidden-password");
    await flushAsyncWork();

    assert.equal(env.primary.value.textContent, "");
    assert.equal(env.primary.panel.hidden, true);
    assert.equal(env.secondary.value.textContent, "");
    assert.equal(env.secondary.panel.hidden, true);
}

async function testPagehideClearsAndRejectsLateResponse() {
    const env = createEnvironment();
    await reveal(env, "visible-before-pagehide");
    const pending = deferredResponse();
    env.queueDeferred(pending);
    env.click(env.primary.reveal);
    env.secondary.value.textContent = "other-pagehide-password";
    env.secondary.panel.hidden = false;

    env.pagehide();
    pending.resolve(200, "late-pagehide-password");
    await flushAsyncWork();

    assert.equal(env.primary.value.textContent, "");
    assert.equal(env.primary.panel.hidden, true);
    assert.equal(env.secondary.value.textContent, "");
    assert.equal(env.secondary.panel.hidden, true);
}

async function testCopyUsesSamePanelWithoutExtendingExpiry() {
    const env = createEnvironment();
    await reveal(env, "copied-password");
    env.secondary.value.textContent = "other-panel-password";
    env.clock.tick(10_000);

    env.click(env.secondary.copy);
    await flushAsyncWork();
    env.click(env.primary.copy);
    await flushAsyncWork();

    assert.deepEqual(
        env.clipboardWrites,
        ["other-panel-password", "copied-password"]);
    assert.equal(env.primary.status.textContent, "已复制");
    assert.equal(env.primary.countdown.textContent, "20");
    env.clock.tick(19_999);
    assert.equal(env.primary.value.textContent, "copied-password");
    assert.equal(env.primary.panel.hidden, false);
    env.clock.tick(1);
    assert.equal(env.primary.value.textContent, "");
    assert.equal(env.primary.panel.hidden, true);
}

async function testForbiddenRedirectKeepsPostContract() {
    const env = createEnvironment();
    env.queueResponse(403);
    env.click(env.primary.reveal);
    await flushAsyncWork();

    assert.deepEqual(
        env.redirects,
        ["/secrets/reauthenticate?returnUrl=%2Fservers"]);
    assert.equal(env.fetchCalls[0].options.method, "POST");
    assert.equal(env.fetchCalls[0].options.credentials, "same-origin");
    assert.equal(
        env.fetchCalls[0].options.body.get("__RequestVerificationToken"),
        "anti-forgery-token");
    assert.equal(env.primary.value.textContent, "");
    assert.equal(env.primary.panel.hidden, true);
}

async function testRepeatedRevealIgnoresStaleForbiddenResponse() {
    const env = createEnvironment();
    const stale = deferredResponse();
    env.queueDeferred(stale);
    env.click(env.primary.reveal);

    env.queueResponse(200, "current-password");
    env.click(env.primary.reveal);
    await flushAsyncWork();
    stale.resolve(403);
    await flushAsyncWork();

    assert.deepEqual(env.redirects, []);
    assert.equal(env.primary.value.textContent, "current-password");
    assert.equal(env.primary.panel.hidden, false);
}

async function testHiddenIgnoresStaleForbiddenResponse() {
    const env = createEnvironment();
    const stale = deferredResponse();
    env.queueDeferred(stale);
    env.click(env.primary.reveal);

    env.hideDocument();
    stale.resolve(403);
    await flushAsyncWork();

    assert.deepEqual(env.redirects, []);
    assert.equal(env.primary.value.textContent, "");
    assert.equal(env.primary.panel.hidden, true);
}

async function testPagehideIgnoresStaleForbiddenResponse() {
    const env = createEnvironment();
    const stale = deferredResponse();
    env.queueDeferred(stale);
    env.click(env.primary.reveal);

    env.pagehide();
    stale.resolve(403);
    await flushAsyncWork();

    assert.deepEqual(env.redirects, []);
    assert.equal(env.primary.value.textContent, "");
    assert.equal(env.primary.panel.hidden, true);
}

async function main() {
    const tests = [
        testRequestContractAndPlaintextDestination,
        testThirtySecondExpiry,
        testRepeatedRevealClearsAndRejectsLateResponse,
        testHiddenClearsAndRejectsLateResponse,
        testPagehideClearsAndRejectsLateResponse,
        testCopyUsesSamePanelWithoutExtendingExpiry,
        testForbiddenRedirectKeepsPostContract,
        testRepeatedRevealIgnoresStaleForbiddenResponse,
        testHiddenIgnoresStaleForbiddenResponse,
        testPagehideIgnoresStaleForbiddenResponse,
    ];
    const failures = [];
    for (const test of tests) {
        try {
            await test();
        } catch (error) {
            failures.push(`${test.name}: ${error.stack ?? error}`);
        }
    }
    if (failures.length > 0) {
        throw new Error(failures.join("\n\n"));
    }
    process.stdout.write("secret-reveal DOM tests passed\n");
}

main().catch(error => {
    console.error(error);
    process.exitCode = 1;
});

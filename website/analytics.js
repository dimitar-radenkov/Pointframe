// Analytics module: attribution, event tracking, debug overlay, and App Insights wiring.
export function createAnalytics() {
    const APP_INSIGHTS_CONNECTION_STRING = "InstrumentationKey=13918c77-74fd-4f9a-a181-e341b8b640b5;IngestionEndpoint=https://germanywestcentral-1.in.applicationinsights.azure.com/;LiveEndpoint=https://germanywestcentral.livediagnostics.monitor.azure.com/;ApplicationId=6733b212-1b3a-482e-97f0-6e21d94207fe";
    const ATTRIBUTION_STORAGE_KEY = "pf_growth_attribution";
    const EVENT_STORAGE_KEY = "pf_growth_events";
    const SESSION_FLAG_KEY = "pf_growth_session_started";
    const UTM_KEYS = ["utm_source", "utm_medium", "utm_campaign", "utm_content", "utm_term"];
    const APP_INSIGHTS_SCRIPT_SRC = "https://js.monitor.azure.com/scripts/b/ai.3.gbl.min.js";
    const DEBUG_QUERY_KEY = "pfDebug";

    let appInsightsClient = null;
    const appInsightsQueue = [];
    let appInsightsStatus = APP_INSIGHTS_CONNECTION_STRING ? "not_initialized" : "disabled";
    const debugMode = new URLSearchParams(window.location.search).get(DEBUG_QUERY_KEY) === "1";
    const debugState = {
        statusNode: null,
        eventCountNode: null,
        attributionNode: null,
        eventsNode: null
    };

    window.pointframeAnalytics = {
        version: "1.0",
        events: ["website_session_started", "website_cta_clicked", "winget_command_copied", "winget_command_copy_failed"]
    };

    const toAppInsightsProperties = (payload) => {
        const properties = {};

        for (const [key, value] of Object.entries(payload)) {
            if (key === "event" || value === null || value === undefined) {
                continue;
            }

            if (typeof value === "string" || typeof value === "number" || typeof value === "boolean") {
                properties[key] = String(value);
                continue;
            }

            properties[key] = JSON.stringify(value);
        }

        return properties;
    };

    const sessionEvents = [];

    const clearLegacyLocalStorage = () => {
        try {
            window.localStorage.removeItem(EVENT_STORAGE_KEY);
            window.localStorage.removeItem(ATTRIBUTION_STORAGE_KEY);
        }
        catch {
            // No-op: storage may be unavailable.
        }
    };

    const readStoredAttribution = () => {
        try {
            const raw = window.sessionStorage.getItem(ATTRIBUTION_STORAGE_KEY);

            if (!raw) {
                return {};
            }

            const parsed = JSON.parse(raw);
            return typeof parsed === "object" && parsed ? parsed : {};
        }
        catch {
            return {};
        }
    };

    const updateDebugOverlay = () => {
        if (!debugMode || !debugState.statusNode || !debugState.eventsNode || !debugState.eventCountNode || !debugState.attributionNode) {
            return;
        }

        const events = sessionEvents;
        const recentEvents = events.slice(-6);
        const attributionSnapshot = readStoredAttribution();

        debugState.statusNode.textContent = appInsightsStatus;
        debugState.eventCountNode.textContent = String(events.length);
        debugState.attributionNode.textContent = Object.keys(attributionSnapshot).length > 0
            ? JSON.stringify(attributionSnapshot)
            : "none";
        debugState.eventsNode.textContent = recentEvents.length > 0
            ? JSON.stringify(recentEvents, null, 2)
            : "(no events yet)";
    };

    const renderDebugOverlay = () => {
        if (!debugMode) {
            return;
        }

        const panel = document.createElement("aside");
        panel.setAttribute("aria-label", "Pointframe analytics debug overlay");
        panel.style.position = "fixed";
        panel.style.right = "16px";
        panel.style.bottom = "16px";
        panel.style.width = "360px";
        panel.style.maxHeight = "62vh";
        panel.style.padding = "12px";
        panel.style.border = "1px solid rgba(255,255,255,0.2)";
        panel.style.borderRadius = "12px";
        panel.style.background = "rgba(17, 24, 39, 0.95)";
        panel.style.backdropFilter = "blur(6px)";
        panel.style.color = "#e5e7eb";
        panel.style.fontFamily = "Consolas, 'Courier New', monospace";
        panel.style.fontSize = "12px";
        panel.style.lineHeight = "1.4";
        panel.style.zIndex = "9999";
        panel.style.overflow = "auto";
        panel.style.boxShadow = "0 12px 28px rgba(0,0,0,0.45)";

        panel.innerHTML = `
            <div style="display:flex;align-items:center;justify-content:space-between;gap:8px;margin-bottom:8px;">
                <strong style="font-size:12px;letter-spacing:0.02em;">Pointframe Analytics Debug</strong>
                <span style="color:#93c5fd;">?${DEBUG_QUERY_KEY}=1</span>
            </div>
            <div style="margin-bottom:4px;">App Insights: <span data-pf-debug="status">${appInsightsStatus}</span></div>
            <div style="margin-bottom:4px;">Events this tab: <span data-pf-debug="count">0</span></div>
            <div style="margin-bottom:8px;">Attribution: <span data-pf-debug="attribution">none</span></div>
            <div style="display:flex;gap:8px;margin-bottom:8px;">
                <button type="button" data-pf-debug="copy" style="padding:4px 8px;border:1px solid rgba(255,255,255,0.25);background:rgba(31,41,55,0.9);color:#f3f4f6;border-radius:6px;cursor:pointer;">Copy JSON</button>
                <button type="button" data-pf-debug="clear" style="padding:4px 8px;border:1px solid rgba(255,255,255,0.25);background:rgba(31,41,55,0.9);color:#f3f4f6;border-radius:6px;cursor:pointer;">Clear Events</button>
            </div>
            <pre data-pf-debug="events" style="margin:0;padding:8px;border:1px solid rgba(255,255,255,0.12);border-radius:8px;background:rgba(3,7,18,0.8);white-space:pre-wrap;word-break:break-word;">(no events yet)</pre>
        `;

        document.body.appendChild(panel);

        debugState.statusNode = panel.querySelector('[data-pf-debug="status"]');
        debugState.eventCountNode = panel.querySelector('[data-pf-debug="count"]');
        debugState.attributionNode = panel.querySelector('[data-pf-debug="attribution"]');
        debugState.eventsNode = panel.querySelector('[data-pf-debug="events"]');

        const copyButton = panel.querySelector('[data-pf-debug="copy"]');
        const clearButton = panel.querySelector('[data-pf-debug="clear"]');

        copyButton?.addEventListener("click", async () => {
            try {
                const payload = {
                    attribution: readStoredAttribution(),
                    events: sessionEvents
                };
                await navigator.clipboard.writeText(JSON.stringify(payload, null, 2));
            }
            catch {
                // No-op in debug tool.
            }
        });

        clearButton?.addEventListener("click", () => {
            sessionEvents.length = 0;
            updateDebugOverlay();
        });

        updateDebugOverlay();
    };

    const flushAppInsightsQueue = () => {
        if (!appInsightsClient || appInsightsQueue.length === 0) {
            return;
        }

        while (appInsightsQueue.length > 0) {
            const queued = appInsightsQueue.shift();

            if (!queued) {
                continue;
            }

            appInsightsClient.trackEvent(
                { name: queued.event },
                toAppInsightsProperties(queued)
            );
        }

        updateDebugOverlay();
    };

    const sendToAppInsights = (payload) => {
        if (!APP_INSIGHTS_CONNECTION_STRING) {
            return;
        }

        if (!appInsightsClient) {
            appInsightsQueue.push(payload);
            return;
        }

        appInsightsClient.trackEvent(
            { name: payload.event },
            toAppInsightsProperties(payload)
        );
    };

    const initAppInsights = () => {
        if (!APP_INSIGHTS_CONNECTION_STRING) {
            updateDebugOverlay();
            return;
        }

        appInsightsStatus = "loading_sdk";
        updateDebugOverlay();

        const script = document.createElement("script");
        script.src = APP_INSIGHTS_SCRIPT_SRC;
        script.async = true;
        script.crossOrigin = "anonymous";

        script.addEventListener("load", () => {
            try {
                const aiRoot = window.Microsoft && window.Microsoft.ApplicationInsights;

                if (!aiRoot || !aiRoot.ApplicationInsights) {
                    appInsightsStatus = "sdk_unavailable";
                    updateDebugOverlay();
                    return;
                }

                const init = new aiRoot.ApplicationInsights({
                    config: {
                        connectionString: APP_INSIGHTS_CONNECTION_STRING,
                        // The site keeps no analytics data in the browser beyond the tab's session ID.
                        disableCookiesUsage: true,
                        isStorageUseDisabled: true,
                        enableSessionStorageBuffer: false
                    }
                });

                appInsightsClient = init.loadAppInsights();
                appInsightsClient.trackPageView();
                appInsightsStatus = "ready";
                flushAppInsightsQueue();
            }
            catch {
                appInsightsStatus = "init_error";
                updateDebugOverlay();
            }
        });

        script.addEventListener("error", () => {
            appInsightsStatus = "script_load_error";
            updateDebugOverlay();
        });

        document.head.appendChild(script);
    };

    const parseUtmParams = () => {
        const params = new URLSearchParams(window.location.search);
        const utm = {};

        for (const key of UTM_KEYS) {
            const value = params.get(key);
            if (value) {
                utm[key] = value;
            }
        }

        return utm;
    };

    const writeStoredAttribution = (attribution) => {
        try {
            window.sessionStorage.setItem(ATTRIBUTION_STORAGE_KEY, JSON.stringify(attribution));
        }
        catch {
            // No-op: session storage should never block user interaction.
        }
    };

    clearLegacyLocalStorage();

    const utmFromUrl = parseUtmParams();
    const storedAttribution = readStoredAttribution();
    const attribution = Object.keys(utmFromUrl).length > 0 ? { ...storedAttribution, ...utmFromUrl } : storedAttribution;

    if (Object.keys(utmFromUrl).length > 0) {
        writeStoredAttribution({
            ...storedAttribution,
            ...utmFromUrl,
            landing_path: window.location.pathname,
            first_seen_at: new Date().toISOString(),
            referrer: document.referrer || null
        });
    }

    const sessionId = (() => {
        try {
            const existing = window.sessionStorage.getItem("pf_growth_session_id");

            if (existing) {
                return existing;
            }

            const generated = (window.crypto && typeof window.crypto.randomUUID === "function")
                ? window.crypto.randomUUID()
                : `${Date.now()}-${Math.random().toString(16).slice(2)}`;

            window.sessionStorage.setItem("pf_growth_session_id", generated);
            return generated;
        }
        catch {
            return `${Date.now()}-${Math.random().toString(16).slice(2)}`;
        }
    })();

    const recordEvent = (payload) => {
        sessionEvents.push(payload);

        if (sessionEvents.length > 100) {
            sessionEvents.shift();
        }
    };

    const trackEvent = (eventName, props = {}) => {
        const payload = {
            event: eventName,
            ts: new Date().toISOString(),
            session_id: sessionId,
            page_path: window.location.pathname,
            page_url: window.location.href,
            ...attribution,
            ...props
        };

        window.dataLayer = window.dataLayer || [];
        window.dataLayer.push(payload);
        recordEvent(payload);
        sendToAppInsights(payload);
        updateDebugOverlay();
        return payload;
    };

    const markSessionStarted = () => {
        try {
            const alreadyTracked = window.sessionStorage.getItem(SESSION_FLAG_KEY) === "1";

            if (alreadyTracked) {
                return;
            }

            trackEvent("website_session_started", {
                referrer: document.referrer || null,
                user_agent: navigator.userAgent
            });
            window.sessionStorage.setItem(SESSION_FLAG_KEY, "1");
        }
        catch {
            // No-op for session storage failures.
        }
    };

    const appendUtmToLinks = () => {
        const links = document.querySelectorAll("a.js-utm-link");

        for (const link of links) {
            if (!(link instanceof HTMLAnchorElement)) {
                continue;
            }

            try {
                const url = new URL(link.href, window.location.origin);
                const source = link.dataset.utmSource || attribution.utm_source || "pointframe_website";
                const medium = link.dataset.utmMedium || attribution.utm_medium || "cta";
                const campaign = link.dataset.utmCampaign || attribution.utm_campaign || "homepage";
                const content = link.dataset.utmContent || attribution.utm_content || null;

                url.searchParams.set("utm_source", source);
                url.searchParams.set("utm_medium", medium);
                url.searchParams.set("utm_campaign", campaign);

                if (content) {
                    url.searchParams.set("utm_content", content);
                }

                link.href = url.toString();
            }
            catch {
                // No-op for invalid URLs.
            }
        }
    };

    const bindTrackedClicks = () => {
        const trackedElements = document.querySelectorAll("[data-track-event]");

        for (const element of trackedElements) {
            element.addEventListener("click", () => {
                const target = element instanceof HTMLAnchorElement ? element.href : null;

                trackEvent(element.getAttribute("data-track-event") || "website_cta_clicked", {
                    cta_type: element.getAttribute("data-cta-type") || "unknown",
                    cta_location: element.getAttribute("data-cta-location") || "unknown",
                    target_url: target
                });
            });
        }
    };

    window.pointframeTrackEvent = trackEvent;
    appendUtmToLinks();
    bindTrackedClicks();
    renderDebugOverlay();
    initAppInsights();
    markSessionStarted();

    return { trackEvent };
}

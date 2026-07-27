// Entrypoint module: composes analytics and UI behaviors for every page.
// Landing pages load this too, so organic sessions and their download CTAs
// are attributed the same way as the homepage.
import { createAnalytics } from "./analytics.js";
import { initCopyCommand } from "./copy-command.js";

const { trackEvent } = createAnalytics();
initCopyCommand(trackEvent);

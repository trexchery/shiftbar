// Shiftbar tab tag: puts the window's short name first in Claude Code's terminal tab title ("74 ✳ Fix the login page"),
// so windows are easy to tell apart. The short name is the end of the name other sessions see it by (drill-desk-74 → 74;
// a name given with CLAUDE_CODE_SESSION_NAME is used whole), read from Claude Code's own record of the window,
// <config dir>/sessions/<pid>.json.
//
// Opt-in, per user: set the user environment variable BUN_OPTIONS=--preload <this file, forward slashes>. Claude Code
// (a Bun program) then loads it first. Claude Code sets the title only through `process.title`, so only that property is
// wrapped; nothing is written to the terminal and nothing else changes. It never throws: on any failure the title is
// left as Claude wrote it. Remove the variable to turn it off. Keep this file where the variable points: Bun refuses to
// start when a preload file is missing.
try {
  const fs = require("fs"), path = require("path"), os = require("os");
  const own = Object.getOwnPropertyDescriptor(process, "title");
  const d = own || Object.getOwnPropertyDescriptor(Object.getPrototypeOf(process), "title");
  if (d && typeof d.get === "function" && typeof d.set === "function") {
    const record = path.join(process.env.CLAUDE_CONFIG_DIR || path.join(os.homedir(), ".claude"), "sessions", `${process.pid}.json`);
    let tag = null, readAt = 0;
    const tagNow = () => {
      // The name can change (a rename, or a clash with another window's): looked up again every 5 seconds at most.
      if (Date.now() - readAt < 5000) return tag;
      readAt = Date.now();
      try {
        const r = JSON.parse(fs.readFileSync(record, "utf8"));
        const name = typeof r.name === "string" ? r.name.trim() : "";
        tag = !name ? null : r.nameSource === "derived" ? name.slice(name.lastIndexOf("-") + 1) || name : name;
      } catch { tag = null; } // no record yet: a launcher, `claude -p`, or a window still starting
      return tag;
    };
    Object.defineProperty(process, "title", {
      configurable: true,
      enumerable: d.enumerable,
      get() { return d.get.call(process); },
      set(v) {
        let t = v;
        try {
          // "" and "claude" are what Claude Code sets at start and exit: left alone.
          const k = typeof v === "string" && v !== "" && v !== "claude" ? tagNow() : null;
          if (k && !v.startsWith(k + " ")) t = `${k} ${v}`;
        } catch {}
        d.set.call(process, t);
      },
    });
  }
} catch {}

// Self-check for tabtag.js, with a synthetic session record in a temp config dir:
//   bun --preload ./dock/tabtag.js dock/tabtag.check.js
const fs = require("fs"), path = require("path"), os = require("os"), assert = require("assert");
const { spawnSync } = require("child_process");

if (!process.env.TABTAG_CASE) {
  const run = (record) => {
    const dir = fs.mkdtempSync(path.join(os.tmpdir(), "tabtag-"));
    const r = spawnSync(process.execPath, ["--preload", path.join(__dirname, "tabtag.js"), __filename],
      { env: { ...process.env, CLAUDE_CONFIG_DIR: dir, TABTAG_CASE: JSON.stringify(record) }, encoding: "utf8" });
    fs.rmSync(dir, { recursive: true, force: true });
    assert.strictEqual(r.status, 0, r.stderr);
    return JSON.parse(r.stdout);
  };
  assert.deepStrictEqual(run({ name: "drill-desk-74", nameSource: "derived" }), ["74 ✳ Fix it", "74 ◐ Fix it", "", "claude"]);
  assert.deepStrictEqual(run({ name: "Billing", }), ["Billing ✳ Fix it", "Billing ◐ Fix it", "", "claude"]); // a given name, whole
  assert.deepStrictEqual(run(null), ["✳ Fix it", "◐ Fix it", "", "claude"]); // no record: untouched
  console.log("tabtag ok");
} else {
  // The child: writes its own record (named by its pid, as Claude Code does), then sets titles as Claude Code would.
  const record = JSON.parse(process.env.TABTAG_CASE);
  if (record) {
    const dir = path.join(process.env.CLAUDE_CONFIG_DIR, "sessions");
    fs.mkdirSync(dir, { recursive: true });
    fs.writeFileSync(path.join(dir, `${process.pid}.json`), JSON.stringify({ pid: process.pid, ...record }));
  }
  const seen = [];
  for (const t of ["✳ Fix it", "◐ Fix it", "", "claude"]) { process.title = t; seen.push(process.title); }
  process.title = seen[0]; // already tagged: not tagged twice
  if (process.title !== seen[0]) seen.push("double: " + process.title);
  process.stdout.write(JSON.stringify(seen));
}

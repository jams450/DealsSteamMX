import test from "node:test";
import assert from "node:assert/strict";
import { readdir, readFile } from "node:fs/promises";

const routes = new URL("../app/api/bff/jobs/", import.meta.url);

test("Jobs detail and dispatch share one Next dynamic segment", async () => {
  const segments = (await readdir(routes)).filter(name => name.startsWith("["));
  assert.deepEqual(segments, ["[job]"]);
  const detail = await readFile(new URL("[job]/route.ts", routes), "utf8");
  const dispatch = await readFile(new URL("[job]/run/route.ts", routes), "utf8");
  assert.match(detail, /export async function GET/);
  assert.match(detail, /const id = Number\(job\)/);
  assert.match(detail, /Number\.isInteger\(id\)/);
  assert.match(detail, /id <= 0/);
  assert.match(dispatch, /export async function POST/);
  assert.match(dispatch, /encodeURIComponent\(job\)/);
});

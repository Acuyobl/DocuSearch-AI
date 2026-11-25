// Regresión de color-scheme y preferencia de tema.
// Requiere E2E_EMAIL / E2E_TOTP_SECRET.
const { test, expect } = require("@playwright/test");
const crypto = require("node:crypto");

function base32Decode(input) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const clean = input.replace(/=+$/, "").toUpperCase();
  let bits = "";
  for (const char of clean) {
    bits += alphabet.indexOf(char).toString(2).padStart(5, "0");
  }
  const bytes = [];
  for (let index = 0; index + 8 <= bits.length; index += 8) {
    bytes.push(parseInt(bits.slice(index, index + 8), 2));
  }
  return Buffer.from(bytes);
}

function totp(secret) {
  const counter = Math.floor(Date.now() / 30_000);
  const buffer = Buffer.alloc(8);
  buffer.writeBigUInt64BE(BigInt(counter));
  const digest = crypto.createHmac("sha1", base32Decode(secret)).update(buffer).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  return String((digest.readUInt32BE(offset) & 0x7fffffff) % 1_000_000).padStart(6, "0");
}

test("el tema actualiza color-scheme y se conserva al recargar", async ({ page }) => {
  test.skip(!process.env.E2E_EMAIL || !process.env.E2E_TOTP_SECRET,
    "Configura E2E_EMAIL y E2E_TOTP_SECRET.");
  await page.goto("/login");
  await page.fill("#email", process.env.E2E_EMAIL);
  await page.fill("#code", totp(process.env.E2E_TOTP_SECRET));
  await page.click('button[type="submit"]');
  await page.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: 30_000 });

  await page.goto("/");
  const meta = page.locator('meta[name="color-scheme"]');
  await expect(meta).toHaveCount(1);

  const before = await page.evaluate(() =>
    document.querySelector('meta[name="color-scheme"]').content);
  expect(["light", "dark"]).toContain(before);

  await page.getByRole("button", { name: "Alternar modo claro u oscuro" }).click();
  const toggled = await page.evaluate(() =>
    document.querySelector('meta[name="color-scheme"]').content);
  expect(toggled).not.toBe(before);
  expect(toggled).toBe(await page.evaluate(() => document.documentElement.dataset.theme));

  await page.reload();
  await expect.poll(() => page.evaluate(() =>
    document.querySelector('meta[name="color-scheme"]').content)).toBe(toggled);
});

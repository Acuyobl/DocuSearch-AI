// Regresión de tabla ordenable en Gestión documental.
const { test, expect } = require("@playwright/test");
const crypto = require("node:crypto");

function base32Decode(input) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const clean = input.replace(/=+$/, "").toUpperCase();
  let bits = "";
  for (const char of clean) bits += alphabet.indexOf(char).toString(2).padStart(5, "0");
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

test("la tabla de documentos ordena por encabezado y expone aria-sort", async ({ page }) => {
  test.skip(!process.env.E2E_EMAIL || !process.env.E2E_TOTP_SECRET,
    "Configura E2E_EMAIL y E2E_TOTP_SECRET.");
  await page.goto("/login");
  await page.fill("#email", process.env.E2E_EMAIL);
  await page.fill("#code", totp(process.env.E2E_TOTP_SECRET));
  await page.click('button[type="submit"]');
  await page.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: 30_000 });
  await page.goto("/management");

  const header = page.getByRole("button", { name: /Código \/ Título/i });
  await expect(header).toBeVisible();
  const th = header.locator("xpath=ancestor::th");
  await expect(th).toHaveAttribute("aria-sort", "none");

  await header.click();
  await expect(th).toHaveAttribute("aria-sort", "ascending");
  await header.click();
  await expect(th).toHaveAttribute("aria-sort", "descending");
});

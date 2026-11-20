// E2E de RBAC: un consultor no debe ver un expediente Confidencial.
// Requiere:
//   E2E_LOW_EMAIL / E2E_LOW_TOTP_SECRET  (usuario con rol consultor)
// La app debe tener ese usuario en Auth:Users y su secreto TOTP configurado.
const { test, expect } = require("@playwright/test");
const crypto = require("node:crypto");

function base32Decode(input) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  const clean = input.replace(/=+$/, "").toUpperCase();
  let bits = "";
  for (const char of clean) {
    const value = alphabet.indexOf(char);
    if (value < 0) throw new Error("Secreto TOTP inválido");
    bits += value.toString(2).padStart(5, "0");
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

test("un consultor no recibe fuentes Confidenciales del asistente", async ({ page }) => {
  test.skip(!process.env.E2E_LOW_EMAIL || !process.env.E2E_LOW_TOTP_SECRET,
    "Configura E2E_LOW_EMAIL y E2E_LOW_TOTP_SECRET.");
  await page.goto("/login");
  await page.fill("#email", process.env.E2E_LOW_EMAIL);
  await page.fill("#code", totp(process.env.E2E_LOW_TOTP_SECRET));
  await page.click('button[type="submit"]');
  await page.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: 30_000 });
  await page.goto("/search");
  await page.getByRole("button", { name: /Asistente IA/i }).click();
  await expect(page.locator("textarea")).toBeVisible({ timeout: 30_000 });
  await expect(page.locator(".agent-status-button")).toBeVisible();

  await page.locator("textarea").fill("¿Qué dice el informe financiero confidencial?");
  await page.getByRole("button", { name: /^Consultar$/ }).click();
  await expect(page.locator(".chat-turn.assistant .answer-body")).toBeVisible({ timeout: 90_000 });

  const answer = await page.locator(".chat-turn.assistant").last().innerText();
  expect(answer).not.toContain("REG-2026-55F63E9C1E3F");
  const sourceCodes = await page.locator(".answer-sources code").allInnerTexts();
  expect(sourceCodes).not.toContain("REG-2026-55F63E9C1E3F");
});

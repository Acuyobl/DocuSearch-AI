// E2E de aprobaciones de doble control.
// Requiere la app levantada, una solicitud pendiente y:
//   E2E_EMAIL / E2E_TOTP_SECRET                    (solicitante, normalmente admin)
//   E2E_AUDITOR_EMAIL / E2E_AUDITOR_TOTP_SECRET    (auditor que puede decidir)
// No contiene credenciales.
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

const lastTotpStep = new Map();

async function login(page, email, secret) {
  const currentStep = Math.floor(Date.now() / 30_000);
  if (lastTotpStep.get(secret) === currentStep) {
    const wait = 30_000 - (Date.now() % 30_000) + 1_000;
    await page.waitForTimeout(wait);
  }
  lastTotpStep.set(secret, Math.floor(Date.now() / 30_000));
  await page.goto("/login");
  await page.fill("#email", email);
  await page.fill("#code", totp(secret));
  await page.click('button[type="submit"]');
  await page.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: 30_000 });
  await page.goto("/audit");
  const approvalsTab = page.getByRole("tab", { name: /Aprobaciones/i });
  await expect(approvalsTab).not.toContainText("@if");
  await approvalsTab.click();
  await expect(page.locator(".approvals-panel")).toBeVisible({ timeout: 30_000 });
}

test.describe.configure({ mode: "serial" });

test("la pestaña Aprobaciones se renderiza correctamente", async ({ page }) => {
  await login(page, process.env.E2E_EMAIL, process.env.E2E_TOTP_SECRET);
  const tab = page.getByRole("tab", { name: /Aprobaciones/i });
  await expect(tab).toHaveText(/Aprobaciones/);
  await expect(tab).not.toContainText("@if");
  await expect(page.locator(".approvals-panel")).toBeVisible();
});

test("el solicitante ve su solicitud pendiente y puede cancelarla", async ({ page }) => {
  test.skip(!process.env.E2E_EMAIL || !process.env.E2E_TOTP_SECRET,
    "Configura E2E_EMAIL y E2E_TOTP_SECRET.");
  await login(page, process.env.E2E_EMAIL, process.env.E2E_TOTP_SECRET);
  const pending = page.locator(".approval-card.pending").first();
  test.skip(await pending.count() === 0, "No hay solicitud pendiente en este entorno.");
  await expect(pending).toBeVisible();
  await expect(pending).toContainText(/Esperando a otra persona/i);
  await expect(pending.getByRole("button", { name: /Cancelar solicitud/i })).toBeVisible();
  await expect(pending.getByRole("button", { name: /Aprobar y ejecutar/i })).toHaveCount(0);
});

test("un auditor ve Aprobar y ejecutar y Rechazar", async ({ browser }) => {
  test.skip(!process.env.E2E_AUDITOR_EMAIL || !process.env.E2E_AUDITOR_TOTP_SECRET,
    "Configura E2E_AUDITOR_EMAIL y E2E_AUDITOR_TOTP_SECRET.");
  const context = await browser.newContext();
  const page = await context.newPage();
  await login(page, process.env.E2E_AUDITOR_EMAIL, process.env.E2E_AUDITOR_TOTP_SECRET);
  const pending = page.locator(".approval-card.pending").first();
  test.skip(await pending.count() === 0, "No hay solicitud pendiente en este entorno.");
  await expect(pending).toBeVisible();
  await expect(pending.getByRole("button", { name: /Aprobar y ejecutar/i })).toBeVisible();
  await expect(pending.getByRole("button", { name: /Rechazar/i })).toBeVisible();
  await context.close();
});

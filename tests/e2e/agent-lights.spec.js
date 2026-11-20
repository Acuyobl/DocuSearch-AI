// Pruebas E2E del indicador de agentes.
// Requiere: app levantada, OpenCode/DeepSeek disponible y variables:
//   E2E_EMAIL, E2E_TOTP_SECRET
// No contiene credenciales ni secretos.
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

function totp(secret, now = Date.now()) {
  const counter = Math.floor(now / 30_000);
  const buffer = Buffer.alloc(8);
  buffer.writeBigUInt64BE(BigInt(counter));
  const digest = crypto.createHmac("sha1", base32Decode(secret)).update(buffer).digest();
  const offset = digest[digest.length - 1] & 0x0f;
  const value = digest.readUInt32BE(offset) & 0x7fffffff;
  return String(value % 1_000_000).padStart(6, "0");
}

async function login(page) {
  const email = process.env.E2E_EMAIL;
  const secret = process.env.E2E_TOTP_SECRET;
  if (!email || !secret) throw new Error("Configura E2E_EMAIL y E2E_TOTP_SECRET.");
  await page.goto("/login");
  await page.fill("#email", email);
  await page.fill("#code", totp(secret));
  await page.click('button[type="submit"]');
  await page.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: 30_000 });
  await page.goto("/search");
  await expect(page.getByRole("button", { name: /Asistente IA/i })).toBeVisible({ timeout: 30_000 });
}

async function openAssistant(page) {
  await page.getByRole("button", { name: /Asistente IA/i }).click();
  await expect(page.locator(".agent-status-button")).toBeVisible({ timeout: 30_000 });
  await expect(page.locator("textarea")).toBeVisible();
}

async function ask(page, question) {
  await page.locator("textarea").fill(question);
  await page.getByRole("button", { name: /^Consultar$/ }).click();
}

test.describe.configure({ mode: "serial" });

test.beforeEach(async ({ page }) => {
  await login(page);
  await openAssistant(page);
});

test("resumen activa 3 agentes, colores distintos y modal visible", async ({ page }) => {
  await ask(page, "genera un resumen de la documentacion de Mesopotamia");

  // Mientras el pipeline está ejecutándose, el botón debe indicarlo.
  await expect(page.locator(".agent-status-button")).toHaveClass(/running/, { timeout: 20_000 });
  await expect(page.locator(".agent-status-button")).toContainText(/Ejecutando agentes/i);

  // El resumen de LangGraph debe terminar con 3 agentes.
  await expect(page.locator(".agent-status-button")).toContainText(/3 agentes/i, { timeout: 210_000 });
  await expect(page.locator(".agent-status-button .agent-lights i.on")).toHaveCount(3);

  const colors = await page.locator(".agent-status-button .agent-lights i.on").evaluateAll((nodes) =>
    nodes.map((node) => getComputedStyle(node).backgroundColor));
  expect(new Set(colors).size).toBe(3);

  await page.locator(".agent-status-button").click();
  const panel = page.locator(".agent-panel-backdrop");
  await expect(panel).toBeVisible();
  await expect(page.locator(".agent-panel")).toBeVisible();
  await expect(page.locator(".agent-run-card")).toHaveCount(3);
  await expect(page.locator(".agent-run-card.on")).toHaveCount(3);
  await expect(page.locator(".agent-run-card")).toContainText([
    /Recuperador/,
    /Redactor/,
    /Verificador/
  ]);

  await page.getByRole("button", { name: "Cerrar panel de agentes" }).click();
  await expect(panel).toBeHidden();
});

test("una consulta normal muestra un solo agente", async ({ page }) => {
  await ask(page, "¿Cuántos documentos hay?");
  await expect(page.locator(".chat-turn.assistant .answer-body")).toBeVisible({ timeout: 60_000 });
  await expect(page.locator(".agent-status-button")).toContainText(/1 agente/i, { timeout: 30_000 });
  await expect(page.locator(".agent-status-button .agent-lights i.on")).toHaveCount(1);
});

// Página web independiente: buscador + asistente.
const { test, expect } = require("@playwright/test");
const crypto = require("node:crypto");

function base32Decode(input) {
  const alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
  let bits = "";
  for (const char of input.replace(/=+$/, "").toUpperCase()) {
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

test("la página Web permite buscar y usar el asistente", async ({ page }) => {
  test.skip(!process.env.E2E_EMAIL || !process.env.E2E_TOTP_SECRET,
    "Configura E2E_EMAIL y E2E_TOTP_SECRET.");
  await page.goto("/login");
  await page.fill("#email", process.env.E2E_EMAIL);
  await page.fill("#code", totp(process.env.E2E_TOTP_SECRET));
  await page.click('button[type="submit"]');
  await page.waitForURL((url) => !url.pathname.startsWith("/login"), { timeout: 30_000 });

  await page.goto("/web");
  await expect(page.locator(".web-topbar")).toBeVisible();
  await expect(page.locator(".web-connected")).toContainText("Conectado");
  await expect(page.locator(".web-user")).toBeVisible();
  await expect(page.getByRole("link", { name: /Volver al panel/i })).toHaveCount(0);
  await expect(page.locator(".web-sidebar")).toBeVisible();
  await expect(page.locator(".agent-status-button")).toBeVisible();
  await expect(page.locator("textarea")).toBeVisible();
  await expect(page.locator(".engine-status")).toBeHidden();
  await expect(page.locator(".assistant-title")).toBeHidden();
  await expect(page.locator(".search-panel")).toBeHidden();
  await expect(page.locator(".search-hero h1")).toBeHidden();
  await expect(page.locator(".search-modes")).toBeHidden();

  // El compositor queda por encima de la conversación, tipo buscador.
  const composerBox = await page.locator("form.chat-composer").boundingBox();
  const conversationBox = await page.locator(".conversation").boundingBox();
  expect(composerBox.y).toBeLessThan(conversationBox.y);
  await expect(page.locator(".conversation")).toHaveCSS("border-top-width", "0px");

  // Al forzar overflow, el composer pasa a fijo abajo.
  await page.evaluate(() => {
    const conversation = document.querySelector(".conversation");
    if (conversation) conversation.style.minHeight = "1400px";
  });
  await expect(page.locator(".web-shell")).toHaveClass(/web-scroll/);
  await expect(page.locator("form.chat-composer")).toHaveCSS("position", "fixed");

  // Menú lateral: cambiar de modo y ocultarlo.
  await page.locator(".web-sidebar").getByRole("button", { name: "Documentos" }).click();
  await expect(page.locator(".central-search input")).toBeVisible();
  await page.locator(".web-sidebar").getByRole("button", { name: "Asistente" }).click();
  await expect(page.locator("textarea")).toBeVisible();
  await page.locator(".web-sidebar-toggle").click();
  await expect(page.locator(".web-sidebar")).toHaveClass(/collapsed/);

  // Y el asistente responde desde la página independiente.
  await page.locator("textarea").fill("¿Cuántos documentos hay?");
  await page.getByRole("button", { name: /^Consultar$/ }).click();
  await expect(page.locator(".chat-turn.assistant .answer-body")).toBeVisible({ timeout: 90_000 });

  await page.goto("/rbac");
  const web = page.getByRole("link", { name: /Web/i });
  await expect(web).toBeVisible();
  await expect(web).toHaveAttribute("href", "/web");
});

// End-to-end smoke test against a running stack (docker compose up, or vite dev + dotnet run).
//   SPXQ_API_KEY=... WEB_URL=http://127.0.0.1:8080 node e2e/smoke.cjs [screenshot.png]
// Uses a Playwright install available to Node (require('playwright')).
const { chromium } = require('playwright');
const assert = require('node:assert/strict');

(async () => {
  const url = process.env.WEB_URL ?? 'http://127.0.0.1:8080';
  const key = process.env.SPXQ_API_KEY;
  assert.ok(key, 'SPXQ_API_KEY required');
  const browser = await chromium.launch();
  try {
    const page = await browser.newPage();
    await page.goto(url);
    await page.getByText('PAPER / RESEARCH ONLY').waitFor();
    await page.locator('input[type=password]').fill(key);
    await page.getByRole('button', { name: 'Connect' }).click();
    await page.getByText('PAPER_ONLY').waitFor();
    await page.getByText('SYNTHETIC (test data, not market data)').first().waitFor();
    await page.getByRole('button', { name: 'Evaluate 0DTE CALL' }).click();
    await page.getByText('Contract selection').waitFor();
    await page.getByText('WorstCaseLossExceeded').waitFor();
    // The key must never be persisted by the page.
    const stored = await page.evaluate(() => JSON.stringify({ ...localStorage }) + JSON.stringify({ ...sessionStorage }));
    assert.ok(!stored.includes(key), 'API key leaked into web storage');
    if (process.argv[2]) await page.screenshot({ path: process.argv[2], fullPage: true });
    console.log('e2e smoke: PASS');
  } finally {
    await browser.close();
  }
})().catch((e) => { console.error('e2e smoke: FAIL', e); process.exit(1); });

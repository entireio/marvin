import { test, expect, type Page } from '@playwright/test';
import AxeBuilder from '@axe-core/playwright';

async function login(page: Page) {
  await page.goto('/login');
  await page.getByRole('button', { name: 'Enter local preview' }).click();
  await expect(page.getByLabel('Message Marvin')).toBeVisible();
}

test('desktop pin preference persists and hover preview overlays without shifting content', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await login(page);
  const sidebar = page.locator('#app-sidebar');
  await expect(sidebar).toHaveCSS('width', '272px');
  await page.getByLabel('Collapse navigation').click();
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-collapsed/);
  await page.mouse.move(700, 400);
  await page.reload();
  await expect(page.getByLabel('Message Marvin')).toBeVisible();
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-collapsed/);
  const before = await page.locator('.workspace').boundingBox();
  await page.mouse.move(2, 300);
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-floating/);
  expect(await page.locator('.workspace').boundingBox()).toEqual(before);
  await page.mouse.move(700, 400);
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-collapsed/);
  await page.getByLabel('Open navigation').click();
  await expect(sidebar.getByRole('link', { name: 'Marvin', exact: true })).toBeFocused();
  await sidebar.locator('.sidebar-help > summary').click();
  await expect(sidebar.locator('.sidebar-help')).toHaveAttribute('open', '');
  await page.keyboard.press('Escape');
  await expect(sidebar.locator('.sidebar-help')).not.toHaveAttribute('open', '');
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-floating/);
  await page.keyboard.press('Escape');
  await expect(page.getByLabel('Open navigation')).toBeFocused();
  await expect(sidebar).toHaveAttribute('inert', '');
  await page.getByLabel('Open navigation').click();
  await page.getByLabel('Pin sidebar').click();
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-pinned/);
  await page.reload();
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-pinned/);
});

test('mobile drawer traps focus, dismisses with Escape and closes after navigation', async ({ page }) => {
  await page.setViewportSize({ width: 390, height: 844 });
  await login(page);
  await page.getByLabel('Open navigation').click();
  const sidebar = page.getByRole('dialog', { name: 'Conversation navigation' });
  await expect(sidebar).toBeVisible();
  await expect(page.locator('.workspace')).toHaveAttribute('inert', '');
  const brand = sidebar.getByRole('link', { name: 'Marvin', exact: true });
  await expect(brand).toBeFocused();
  await page.evaluate(() => Promise.all(document.getAnimations().filter(a => Number.isFinite(a.effect?.getComputedTiming().endTime ?? Infinity)).map(a => a.finished.catch(() => undefined))));
  const report = await new AxeBuilder({ page }).withTags(['wcag2a', 'wcag2aa', 'wcag21aa']).analyze();
  expect(report.violations.filter(v => ['serious', 'critical'].includes(v.impact ?? '')).map(v => ({id:v.id,nodes:v.nodes.map(n=>({target:n.target,summary:n.failureSummary}))}))).toEqual([]);
  await page.keyboard.press('Shift+Tab');
  await expect(sidebar.locator('.sidebar-pet-link').last()).toBeFocused();
  await page.keyboard.press('Tab');
  await expect(brand).toBeFocused();
  await page.keyboard.press('Escape');
  await expect(sidebar).not.toBeVisible();
  await expect(page.getByLabel('Open navigation')).toBeFocused();
  await page.getByLabel('Open navigation').click();
  await sidebar.getByLabel('New conversation', { exact: true }).click();
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-mobile/);
  await expect(page.getByLabel('Message Marvin')).toBeVisible();
  await page.getByLabel('Open navigation').click();
  await page.mouse.click(370, 400);
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-mobile/);
});

test('viewport changes clear transient navigation without losing the desktop preference', async ({ page }) => {
  await page.setViewportSize({ width: 1440, height: 1000 });
  await login(page);
  await page.setViewportSize({ width: 390, height: 844 });
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-mobile/);
  await page.getByLabel('Open navigation').click();
  await page.setViewportSize({ width: 1440, height: 1000 });
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-pinned/);
  await page.getByLabel('Collapse navigation').click();
  await page.mouse.move(700, 400);
  await page.setViewportSize({ width: 390, height: 844 });
  await page.setViewportSize({ width: 1440, height: 1000 });
  await expect(page.locator('.app-layout')).toHaveClass(/sidebar-collapsed/);
});

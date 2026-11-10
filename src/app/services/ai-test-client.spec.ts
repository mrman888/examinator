import { afterEach, describe, expect, it, vi } from 'vitest';
import { sendTestPrompt } from './ai-test-client';

afterEach(() => vi.unstubAllGlobals());

describe('AI test client', () => {
  it('sends a same-origin JSON request and reads text', async () => {
    const fetchMock = vi.fn().mockResolvedValue(Response.json({ text: 'Antwoord' }));
    vi.stubGlobal('fetch', fetchMock);
    expect(await sendTestPrompt('Vraag', new AbortController().signal)).toBe('Antwoord');
    expect(fetchMock).toHaveBeenCalledWith('/api/test', expect.objectContaining({
      method: 'POST', credentials: 'same-origin', redirect: 'error',
      headers: { 'Content-Type': 'application/json', 'X-Examinator-Request': '1' },
      body: JSON.stringify({ prompt: 'Vraag' }),
    }));
  });

  it.each([401, 403, 429, 502, 504])('reports HTTP %s instead of returning a fake reply', async status => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('', { status })));
    await expect(sendTestPrompt('Vraag', new AbortController().signal)).rejects.toThrow();
  });

  it.each([{}, { text: '' }, { text: 42 }])('rejects invalid reply %j', async reply => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(Response.json(reply)));
    await expect(sendTestPrompt('Vraag', new AbortController().signal)).rejects.toThrow('geen geldig tekstantwoord');
  });

  it('does not treat a sign-in HTML page as a successful response', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue(new Response('<html>Sign in</html>')));
    await expect(sendTestPrompt('Vraag', new AbortController().signal)).rejects.toThrow('Geen geldig API-antwoord');
  });
});

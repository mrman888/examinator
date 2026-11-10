export async function sendTestPrompt(prompt: string, signal: AbortSignal): Promise<string> {
  const response = await fetch('/api/test', {
    method: 'POST',
    credentials: 'same-origin',
    redirect: 'error',
    headers: { 'Content-Type': 'application/json', 'X-Examinator-Request': '1' },
    body: JSON.stringify({ prompt }),
    signal,
  });
  if (!response.ok) {
    const messages: Record<number, string> = {
      400: 'Voer een vraag van maximaal 2000 tekens in.',
      401: 'Je sessie is verlopen. Meld je opnieuw aan.',
      403: 'Je hebt geen toegang tot deze functie. Meld je aan met een toegestaan account.',
      429: 'Te veel verzoeken. Wacht een minuut en probeer opnieuw.',
      502: 'De AI-service is niet beschikbaar. Probeer het later opnieuw.',
      504: 'Het verzoek duurde te lang. Probeer het opnieuw.',
    };
    throw new Error(messages[response.status] ?? `Het verzoek is mislukt (HTTP ${response.status}).`);
  }
  if (!response.headers.get('content-type')?.includes('application/json')) {
    throw new Error('Geen geldig API-antwoord ontvangen. Meld je opnieuw aan of controleer de API-configuratie.');
  }
  const result: unknown = await response.json();
  if (typeof result !== 'object' || result === null || !('text' in result) ||
      typeof result.text !== 'string' || !result.text.trim()) {
    throw new Error('De AI-service heeft geen geldig tekstantwoord teruggestuurd.');
  }
  return result.text;
}

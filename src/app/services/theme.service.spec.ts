import { DOCUMENT } from '@angular/common';
import { TestBed } from '@angular/core/testing';
import { describe, expect, it } from 'vitest';
import { THEME_STORAGE_KEY, ThemeService } from './theme.service';

function setup(storage: Partial<Storage>) {
  const documentElement = document.createElement('html');
  TestBed.configureTestingModule({
    providers: [{ provide: DOCUMENT, useValue: { documentElement, defaultView: { localStorage: storage } } }],
  });
  return { service: TestBed.inject(ThemeService), documentElement };
}

function memoryStorage(initial: Record<string, string> = {}): Partial<Storage> & { data: Record<string, string> } {
  const data = { ...initial };
  return {
    data,
    getItem: (key) => data[key] ?? null,
    setItem: (key, value) => { data[key] = value; },
    removeItem: (key) => { delete data[key]; },
  };
}

describe('ThemeService', () => {
  it('starts on system without a data-theme attribute', () => {
    const { service, documentElement } = setup(memoryStorage());
    expect(service.preference()).toBe('system');
    expect(documentElement.hasAttribute('data-theme')).toBe(false);
  });

  it('cycles system, light, dark and back, updating attribute and storage', () => {
    const storage = memoryStorage();
    const { service, documentElement } = setup(storage);

    service.cycle();
    expect(service.preference()).toBe('light');
    expect(documentElement.getAttribute('data-theme')).toBe('light');
    expect(storage.data[THEME_STORAGE_KEY]).toBe('light');

    service.cycle();
    expect(service.preference()).toBe('dark');
    expect(documentElement.getAttribute('data-theme')).toBe('dark');
    expect(storage.data[THEME_STORAGE_KEY]).toBe('dark');

    service.cycle();
    expect(service.preference()).toBe('system');
    expect(documentElement.hasAttribute('data-theme')).toBe(false);
    expect(THEME_STORAGE_KEY in storage.data).toBe(false);
  });

  it('restores a saved preference and applies it', () => {
    const { service, documentElement } = setup(memoryStorage({ [THEME_STORAGE_KEY]: 'dark' }));
    expect(service.preference()).toBe('dark');
    expect(documentElement.getAttribute('data-theme')).toBe('dark');
  });

  it('ignores an invalid saved value', () => {
    const { service } = setup(memoryStorage({ [THEME_STORAGE_KEY]: 'neon' }));
    expect(service.preference()).toBe('system');
  });

  it('keeps working when storage throws', () => {
    const failing: Partial<Storage> = {
      getItem: () => { throw new Error('geblokkeerd'); },
      setItem: () => { throw new Error('geblokkeerd'); },
      removeItem: () => { throw new Error('geblokkeerd'); },
    };
    const { service, documentElement } = setup(failing);
    expect(service.preference()).toBe('system');
    service.cycle();
    expect(service.preference()).toBe('light');
    expect(documentElement.getAttribute('data-theme')).toBe('light');
    service.set('system');
    expect(documentElement.hasAttribute('data-theme')).toBe(false);
  });
});

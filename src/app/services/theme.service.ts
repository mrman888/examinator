import { DOCUMENT } from '@angular/common';
import { Injectable, inject, signal } from '@angular/core';

export type ThemePreference = 'system' | 'light' | 'dark';

export const THEME_STORAGE_KEY = 'examinator-theme';
const ORDER: readonly ThemePreference[] = ['system', 'light', 'dark'];

@Injectable({ providedIn: 'root' })
export class ThemeService {
  private readonly document = inject(DOCUMENT);
  private readonly state = signal<ThemePreference>(this.load());

  readonly preference = this.state.asReadonly();

  constructor() {
    this.apply(this.state());
  }

  set(preference: ThemePreference): void {
    this.state.set(preference);
    this.apply(preference);
    this.save(preference);
  }

  cycle(): void {
    this.set(ORDER[(ORDER.indexOf(this.state()) + 1) % ORDER.length]);
  }

  private apply(preference: ThemePreference): void {
    const root = this.document.documentElement;
    if (preference === 'system') root.removeAttribute('data-theme');
    else root.setAttribute('data-theme', preference);
  }

  private load(): ThemePreference {
    try {
      const stored = this.document.defaultView?.localStorage.getItem(THEME_STORAGE_KEY);
      return stored === 'light' || stored === 'dark' ? stored : 'system';
    } catch {
      return 'system';
    }
  }

  private save(preference: ThemePreference): void {
    try {
      const storage = this.document.defaultView?.localStorage;
      if (preference === 'system') storage?.removeItem(THEME_STORAGE_KEY);
      else storage?.setItem(THEME_STORAGE_KEY, preference);
    } catch {
      // Opslag kan geblokkeerd zijn; de voorkeur blijft dan alleen voor deze sessie.
    }
  }
}

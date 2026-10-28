import { Injectable, computed, inject, signal } from '@angular/core';
import type { AccountInfo } from '@azure/msal-browser';
import { AuthConfig, parseAuthConfig } from './auth-config';
import { AUTH_CONFIG_SOURCE, MSAL_CLIENT_FACTORY, MsalClient } from './msal-client';

/**
 * - `unavailable`: no valid `auth-config.json`; the app runs in demo mode.
 * - `signed-out` / `signed-in`: sign-in is configured.
 */
export type AuthStatus = 'initializing' | 'unavailable' | 'signed-out' | 'signed-in';

/** No access token without the user signing in again (expired session, revoked consent). */
export class SignInRequiredError extends Error {
  constructor() {
    super('Sign in again to continue.');
    this.name = 'SignInRequiredError';
  }
}

/**
 * Microsoft sign-in for the browser. The API decides who the user is and
 * what they may do; this service only obtains access tokens for the API's
 * scope. The account name here is for display.
 */
@Injectable({ providedIn: 'root' })
export class AuthService {
  private readonly loadConfig = inject(AUTH_CONFIG_SOURCE);
  private readonly createClient = inject(MSAL_CLIENT_FACTORY);

  private client: MsalClient | null = null;
  private config: AuthConfig | null = null;

  private readonly account = signal<AccountInfo | null>(null);

  readonly status = signal<AuthStatus>('initializing');
  readonly accountName = computed(() => this.account()?.name ?? this.account()?.username ?? null);
  /** A sign-in that came back with an error, for example because the user cancelled. */
  readonly signInError = signal<string | null>(null);

  /** Runs once before the app starts (provideAppInitializer). Never throws. */
  async initialize(): Promise<void> {
    let config: AuthConfig | null;

    try {
      config = parseAuthConfig(await this.loadConfig());
    } catch {
      config = null;
    }

    if (config === null) {
      this.status.set('unavailable');
      return;
    }

    this.config = config;

    try {
      this.client = await this.createClient(config);
    } catch {
      this.status.set('unavailable');
      return;
    }

    try {
      // Completes a sign-in that redirected back to this page.
      const result = await this.client.handleRedirectPromise();
      const account =
        result?.account ??
        this.client.getActiveAccount() ??
        this.client.getAllAccounts()[0] ??
        null;

      this.client.setActiveAccount(account);
      this.account.set(account);
      this.status.set(account ? 'signed-in' : 'signed-out');
    } catch (error) {
      this.account.set(null);
      this.status.set('signed-out');
      this.signInError.set(describeAuthError(error));
    }
  }

  async signIn(): Promise<void> {
    if (this.status() !== 'signed-out' || !this.client || !this.config) {
      return;
    }

    this.signInError.set(null);
    await this.client.loginRedirect({ scopes: [this.config.apiScope], prompt: 'select_account' });
  }

  async signOut(): Promise<void> {
    if (this.status() !== 'signed-in' || !this.client) {
      return;
    }

    const account = this.account();

    // No request is sent as this user from here on, even before the redirect.
    this.account.set(null);
    this.status.set('signed-out');
    await this.client.logoutRedirect({ account });
  }

  /**
   * An access token for the API, renewed silently when needed. When the
   * user must interact (expired session, revoked consent), it starts a
   * redirect to Microsoft and rejects with SignInRequiredError.
   */
  async accessToken(): Promise<string> {
    const account = this.account();

    if (this.status() !== 'signed-in' || !this.client || !this.config || !account) {
      throw new SignInRequiredError();
    }

    const request = { scopes: [this.config.apiScope], account };

    try {
      return (await this.client.acquireTokenSilent(request)).accessToken;
    } catch (error) {
      if (this.client.isInteractionRequired(error)) {
        await this.client.acquireTokenRedirect(request);
        throw new SignInRequiredError();
      }

      throw error;
    }
  }
}

function describeAuthError(error: unknown): string {
  const code =
    typeof error === 'object' && error !== null && 'errorCode' in error
      ? String(error.errorCode)
      : '';

  return code === 'user_cancelled' || code === 'access_denied'
    ? 'Sign-in was cancelled.'
    : 'Microsoft sign-in did not complete. Try again.';
}

import { HttpClient, provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { authInterceptor, isOwnApiRequest } from './auth.interceptor';
import { AuthService, AuthStatus } from './auth.service';

describe('authInterceptor', () => {
  const status = signal<AuthStatus>('signed-in');
  let http: HttpClient;
  let controller: HttpTestingController;

  beforeEach(() => {
    status.set('signed-in');

    TestBed.configureTestingModule({
      providers: [
        provideHttpClient(withInterceptors([authInterceptor])),
        provideHttpClientTesting(),
        { provide: AuthService, useValue: { status, accessToken: async () => 'access-token' } },
      ],
    });

    http = TestBed.inject(HttpClient);
    controller = TestBed.inject(HttpTestingController);
  });

  afterEach(() => controller.verify());

  async function authorizationOf(url: string): Promise<string | null> {
    http.get(url).subscribe();
    // The token is fetched asynchronously before the request is sent.
    await new Promise((resolve) => setTimeout(resolve));
    const request = controller.expectOne(url);
    request.flush([]);

    return request.request.headers.get('Authorization');
  }

  it("adds the token to the app's own API requests while signed in", async () => {
    expect(await authorizationOf('/api/organizations')).toBe('Bearer access-token');
  });

  it('never sends the token to another address', async () => {
    expect(await authorizationOf('https://example.com/api/organizations')).toBeNull();
    expect(await authorizationOf('//example.com/api/organizations')).toBeNull();
    expect(await authorizationOf('auth-config.json')).toBeNull();
  });

  it('sends nothing when signed out', async () => {
    status.set('signed-out');

    expect(await authorizationOf('/api/bookings')).toBeNull();
  });

  it('recognises only relative /api/ paths as the own API', () => {
    expect(isOwnApiRequest('/api/me')).toBe(true);
    expect(isOwnApiRequest('/apiary')).toBe(false);
    expect(isOwnApiRequest('http://localhost:5003/api/me')).toBe(false);
  });
});

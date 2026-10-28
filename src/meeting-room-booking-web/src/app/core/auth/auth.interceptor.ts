import { HttpInterceptorFn } from '@angular/common/http';
import { inject } from '@angular/core';
import { from, switchMap } from 'rxjs';
import { AuthService } from './auth.service';

/**
 * Only relative `/api/...` URLs: the app's own origin, which the
 * development server proxies to the API. A token is never attached to an
 * absolute URL, so a misconfigured or injected address cannot receive it.
 */
export function isOwnApiRequest(url: string): boolean {
  return url.startsWith('/api/');
}

/** Adds the access token to the app's own API requests while signed in. */
export const authInterceptor: HttpInterceptorFn = (request, next) => {
  const auth = inject(AuthService);

  if (auth.status() !== 'signed-in' || !isOwnApiRequest(request.url)) {
    return next(request);
  }

  return from(auth.accessToken()).pipe(
    switchMap((token) => next(request.clone({ setHeaders: { Authorization: `Bearer ${token}` } }))),
  );
};

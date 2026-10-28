import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { CurrentUser, Organization, RegistrationResponse } from '../models/account.model';

/** The signed-in user's identity, registration and organisations. */
@Injectable({
  providedIn: 'root',
})
export class AccountApiService {
  private readonly httpClient = inject(HttpClient);

  getCurrentUser(): Observable<CurrentUser> {
    return this.httpClient.get<CurrentUser>('/api/me');
  }

  /** Idempotent per identity: a repeat returns the same user. */
  register(): Observable<RegistrationResponse> {
    return this.httpClient.post<RegistrationResponse>('/api/me/registration', null);
  }

  getOrganizations(): Observable<Organization[]> {
    return this.httpClient.get<Organization[]>('/api/organizations');
  }
}

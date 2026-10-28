import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Booking } from '../models/booking.model';
import { MeetingRoom } from '../models/meeting-room.model';
import { SaveBookingRequest } from '../models/save-booking-request.model';

/**
 * Which rooms and bookings a request is about: an organisation the user is
 * a member of (signed in), or null for the original `/api/rooms` and
 * `/api/bookings` endpoints (the API's anonymous demo mode).
 */
export type BookingScope = { readonly organizationId: string } | null;

@Injectable({
  providedIn: 'root',
})
export class BookingApiService {
  private readonly httpClient = inject(HttpClient);

  getRooms(scope: BookingScope = null): Observable<MeetingRoom[]> {
    return this.httpClient.get<MeetingRoom[]>(`${basePath(scope)}/rooms`);
  }

  getAvailableRooms(
    startUtc: string,
    endUtc: string,
    minCapacity = 1,
    scope: BookingScope = null,
  ): Observable<MeetingRoom[]> {
    return this.httpClient.get<MeetingRoom[]>(`${basePath(scope)}/rooms/available`, {
      params: { startUtc, endUtc, minCapacity: String(minCapacity) },
    });
  }

  getBookings(scope: BookingScope = null): Observable<Booking[]> {
    return this.httpClient.get<Booking[]>(`${basePath(scope)}/bookings`);
  }

  getBooking(bookingId: number, scope: BookingScope = null): Observable<Booking> {
    return this.httpClient.get<Booking>(`${basePath(scope)}/bookings/${bookingId}`);
  }

  createBooking(request: SaveBookingRequest, scope: BookingScope = null): Observable<Booking> {
    return this.httpClient.post<Booking>(`${basePath(scope)}/bookings`, request);
  }

  updateBooking(
    bookingId: number,
    request: SaveBookingRequest,
    scope: BookingScope = null,
  ): Observable<Booking> {
    return this.httpClient.put<Booking>(`${basePath(scope)}/bookings/${bookingId}`, request);
  }

  deleteBooking(bookingId: number, scope: BookingScope = null): Observable<void> {
    return this.httpClient.delete<void>(`${basePath(scope)}/bookings/${bookingId}`);
  }
}

function basePath(scope: BookingScope): string {
  return scope === null ? '/api' : `/api/organizations/${encodeURIComponent(scope.organizationId)}`;
}

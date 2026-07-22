import { HttpClient } from '@angular/common/http';
import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { Booking } from '../models/booking.model';
import { MeetingRoom } from '../models/meeting-room.model';
import { SaveBookingRequest } from '../models/save-booking-request.model';

@Injectable({
  providedIn: 'root',
})
export class BookingApiService {
  private readonly httpClient = inject(HttpClient);

  private readonly roomsUrl = '/api/rooms';
  private readonly bookingsUrl = '/api/bookings';

  getRooms(): Observable<MeetingRoom[]> {
    return this.httpClient.get<MeetingRoom[]>(this.roomsUrl);
  }

  getBookings(): Observable<Booking[]> {
    return this.httpClient.get<Booking[]>(this.bookingsUrl);
  }

  getBooking(bookingId: number): Observable<Booking> {
    return this.httpClient.get<Booking>(`${this.bookingsUrl}/${bookingId}`);
  }

  createBooking(request: SaveBookingRequest): Observable<Booking> {
    return this.httpClient.post<Booking>(this.bookingsUrl, request);
  }

  updateBooking(bookingId: number, request: SaveBookingRequest): Observable<Booking> {
    return this.httpClient.put<Booking>(`${this.bookingsUrl}/${bookingId}`, request);
  }

  deleteBooking(bookingId: number): Observable<void> {
    return this.httpClient.delete<void>(`${this.bookingsUrl}/${bookingId}`);
  }
}

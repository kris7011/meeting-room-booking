import { provideHttpClient } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { TestBed } from '@angular/core/testing';
import { Booking } from '../models/booking.model';
import { MeetingRoom } from '../models/meeting-room.model';
import { SaveBookingRequest } from '../models/save-booking-request.model';
import { BookingApiService } from './booking-api.service';

describe('BookingApiService', () => {
    let service: BookingApiService;
    let httpTestingController: HttpTestingController;

    const booking: Booking = {
        id: 7,
        roomId: 1,
        roomName: 'Focus Room',
        title: 'Sprint planning',
        bookedBy: 'Kris Larsen',
        startUtc: '2030-01-10T09:00:00+00:00',
        endUtc: '2030-01-10T10:00:00+00:00',
        createdUtc: '2030-01-01T12:00:00+00:00',
    };

    const request: SaveBookingRequest = {
        roomId: 1,
        title: 'Sprint planning',
        bookedBy: 'Kris Larsen',
        startUtc: '2030-01-10T09:00:00.000Z',
        endUtc: '2030-01-10T10:00:00.000Z',
    };

    beforeEach(() => {
        TestBed.configureTestingModule({
            providers: [BookingApiService, provideHttpClient(), provideHttpClientTesting()],
        });

        service = TestBed.inject(BookingApiService);
        httpTestingController = TestBed.inject(HttpTestingController);
    });

    afterEach(() => {
        httpTestingController.verify();
    });

    it('should get all meeting rooms', () => {
        const rooms: MeetingRoom[] = [
            {
                id: 1,
                name: 'Focus Room',
                capacity: 4,
            },
            {
                id: 2,
                name: 'Collaboration Room',
                capacity: 8,
            },
        ];

        service.getRooms().subscribe((response) => {
            expect(response).toEqual(rooms);
        });

        const httpRequest = httpTestingController.expectOne('/api/rooms');

        expect(httpRequest.request.method).toBe('GET');

        httpRequest.flush(rooms);
    });

    it('should get all bookings', () => {
        service.getBookings().subscribe((response) => {
            expect(response).toEqual([booking]);
        });

        const httpRequest = httpTestingController.expectOne('/api/bookings');

        expect(httpRequest.request.method).toBe('GET');

        httpRequest.flush([booking]);
    });

    it('should get one booking by id', () => {
        service.getBooking(booking.id).subscribe((response) => {
            expect(response).toEqual(booking);
        });

        const httpRequest = httpTestingController.expectOne(`/api/bookings/${booking.id}`);

        expect(httpRequest.request.method).toBe('GET');

        httpRequest.flush(booking);
    });

    it('should create a booking', () => {
        service.createBooking(request).subscribe((response) => {
            expect(response).toEqual(booking);
        });

        const httpRequest = httpTestingController.expectOne('/api/bookings');

        expect(httpRequest.request.method).toBe('POST');
        expect(httpRequest.request.body).toEqual(request);

        httpRequest.flush(booking, {
            status: 201,
            statusText: 'Created',
        });
    });

    it('should update a booking', () => {
        service.updateBooking(booking.id, request).subscribe((response) => {
            expect(response).toEqual(booking);
        });

        const httpRequest = httpTestingController.expectOne(`/api/bookings/${booking.id}`);

        expect(httpRequest.request.method).toBe('PUT');
        expect(httpRequest.request.body).toEqual(request);

        httpRequest.flush(booking);
    });

    it('should delete a booking', () => {
        let requestCompleted = false;

        service.deleteBooking(booking.id).subscribe({
            complete: () => {
                requestCompleted = true;
            },
        });

        const httpRequest = httpTestingController.expectOne(`/api/bookings/${booking.id}`);

        expect(httpRequest.request.method).toBe('DELETE');

        httpRequest.flush(null, {
            status: 204,
            statusText: 'No Content',
        });

        expect(requestCompleted).toBe(true);
    });
});

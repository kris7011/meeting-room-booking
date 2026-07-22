import { TestBed } from '@angular/core/testing';
import { of } from 'rxjs';
import { Booking } from './core/models/booking.model';
import { MeetingRoom } from './core/models/meeting-room.model';
import { SaveBookingRequest } from './core/models/save-booking-request.model';
import { BookingApiService } from './core/services/booking-api.service';
import { App } from './app';

describe('App', () => {
  const room: MeetingRoom = {
    id: 1,
    name: 'Focus Room',
    capacity: 4,
  };

  const booking: Booking = {
    id: 7,
    roomId: room.id,
    roomName: room.name,
    title: 'Sprint planning',
    bookedBy: 'Kris Larsen',
    startUtc: '2030-01-10T09:00:00.000Z',
    endUtc: '2030-01-10T10:00:00.000Z',
    createdUtc: '2030-01-01T12:00:00.000Z',
  };

  const bookingApiServiceStub = {
    getRooms: () => of([room]),
    getBookings: () => of([booking]),
    getBooking: () => of(booking),
    createBooking: (_request: SaveBookingRequest) => of(booking),
    updateBooking: (_bookingId: number, _request: SaveBookingRequest) => of(booking),
    deleteBooking: (_bookingId: number) => of(void 0),
  };

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        {
          provide: BookingApiService,
          useValue: bookingApiServiceStub,
        },
      ],
    }).compileComponents();
  });

  it('should create the application and render loaded bookings', async () => {
    const fixture = TestBed.createComponent(App);

    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;

    expect(fixture.componentInstance).toBeTruthy();
    expect(compiled.querySelector('h1')?.textContent).toContain('Meeting room booking');
    expect(compiled.textContent).toContain('Sprint planning');
    expect(compiled.textContent).toContain('Focus Room');
  });

  it('should populate the form when edit is selected', async () => {
    const fixture = TestBed.createComponent(App);

    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    const compiled = fixture.nativeElement as HTMLElement;

    const editButton = compiled.querySelector(
      '[data-testid="edit-booking-7"]',
    ) as HTMLButtonElement | null;

    expect(editButton).not.toBeNull();

    editButton?.click();
    fixture.detectChanges();

    const titleInput = compiled.querySelector('#booking-title') as HTMLInputElement | null;

    expect(compiled.textContent).toContain('Edit booking');
    expect(titleInput?.value).toBe('Sprint planning');
  });
});

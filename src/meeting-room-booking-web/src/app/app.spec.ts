import { signal } from '@angular/core';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of } from 'rxjs';
import { AuthService, AuthStatus } from './core/auth/auth.service';
import { CurrentUser, Organization } from './core/models/account.model';
import { Booking } from './core/models/booking.model';
import { MeetingRoom } from './core/models/meeting-room.model';
import { SaveBookingRequest } from './core/models/save-booking-request.model';
import { AccountApiService } from './core/services/account-api.service';
import { BookingApiService, BookingScope } from './core/services/booking-api.service';
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

  const acme: Organization = { publicId: 'org-acme', name: 'Acme', isLegacy: false, role: 'Member' };
  const zeta: Organization = { publicId: 'org-zeta', name: 'Zeta', isLegacy: false, role: 'Owner' };

  const registered: CurrentUser = {
    displayName: 'Mia Member',
    user: { publicId: 'user-1', displayName: 'Mia Member' },
  };

  const status = signal<AuthStatus>('unavailable');
  let bookingsFor: (scope: BookingScope) => Observable<Booking[]>;

  const account = {
    getCurrentUser: vi.fn<() => Observable<CurrentUser>>(),
    register: vi.fn(() => of({ created: true, currentUser: registered })),
    getOrganizations: vi.fn<() => Observable<Organization[]>>(),
  };

  const bookingApi = {
    getRooms: vi.fn((_scope: BookingScope) => of([room])),
    getBookings: vi.fn((scope: BookingScope) => bookingsFor(scope)),
    getBooking: vi.fn(() => of(booking)),
    createBooking: vi.fn((_request: SaveBookingRequest) => of(booking)),
    updateBooking: vi.fn((_bookingId: number, _request: SaveBookingRequest) => of(booking)),
    deleteBooking: vi.fn((_bookingId: number) => of(void 0)),
  };

  beforeEach(async () => {
    vi.clearAllMocks();
    status.set('unavailable');
    bookingsFor = () => of([booking]);
    account.getCurrentUser.mockReturnValue(of(registered));
    account.getOrganizations.mockReturnValue(of([acme, zeta]));

    await TestBed.configureTestingModule({
      imports: [App],
      providers: [
        { provide: BookingApiService, useValue: bookingApi },
        { provide: AccountApiService, useValue: account },
        {
          provide: AuthService,
          useValue: {
            status,
            accountName: signal('Mia Member'),
            signInError: signal(null),
            signIn: vi.fn(),
            signOut: vi.fn(),
          },
        },
      ],
    }).compileComponents();
  });

  async function render() {
    const fixture = TestBed.createComponent(App);

    fixture.detectChanges();
    await fixture.whenStable();
    fixture.detectChanges();

    return { fixture, compiled: fixture.nativeElement as HTMLElement };
  }

  describe('without sign-in (demo mode)', () => {
    it('should create the application and render loaded bookings', async () => {
      const { fixture, compiled } = await render();

      expect(fixture.componentInstance).toBeTruthy();
      expect(compiled.querySelector('h1')?.textContent).toContain('Meeting room booking');
      expect(compiled.textContent).toContain('Sprint planning');
      expect(compiled.textContent).toContain('Focus Room');
      expect(compiled.textContent).toContain('Demo mode.');
      expect(bookingApi.getBookings).toHaveBeenCalledWith(null);
      expect(account.getCurrentUser).not.toHaveBeenCalled();
    });

    it('should populate the form when edit is selected', async () => {
      const { fixture, compiled } = await render();

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

    it('offers Microsoft sign-in when it is configured', async () => {
      status.set('signed-out');

      const { compiled } = await render();

      expect(compiled.querySelector('[data-testid="sign-in"]')).not.toBeNull();
    });
  });

  describe('signed in', () => {
    beforeEach(() => status.set('signed-in'));

    it('loads the first organisation through its own endpoints', async () => {
      const { compiled } = await render();

      expect(account.register).not.toHaveBeenCalled();
      expect(bookingApi.getRooms).toHaveBeenCalledWith({ organizationId: 'org-acme' });
      expect(bookingApi.getBookings).toHaveBeenCalledWith({ organizationId: 'org-acme' });
      expect(compiled.querySelector('[data-testid="account-name"]')?.textContent).toContain(
        'Mia Member',
      );
      expect((compiled.querySelector('#organization') as HTMLSelectElement).options.length).toBe(2);
    });

    it('registers a new user once, before asking for their organisations', async () => {
      account.getCurrentUser.mockReturnValue(of({ displayName: 'New', user: null }));

      await render();

      expect(account.register).toHaveBeenCalledTimes(1);
      expect(account.getOrganizations).toHaveBeenCalled();
    });

    it('explains what to do without an organisation, and loads no bookings', async () => {
      account.getOrganizations.mockReturnValue(of([]));

      const { compiled } = await render();

      expect(compiled.querySelector('[data-testid="account-message"]')?.textContent).toContain(
        'not a member of an organisation',
      );
      expect(bookingApi.getBookings).not.toHaveBeenCalled();
    });

    it("shows other people's bookings as 'Booked', without organiser or actions", async () => {
      bookingsFor = () =>
        of([
          { ...booking, id: 1, title: null, bookedBy: null, detailsHidden: true, canModify: false },
          { ...booking, id: 2, title: 'My meeting', canModify: true, createdByCurrentUser: true },
        ]);

      const { compiled } = await render();

      expect(compiled.querySelector('[data-testid="booking-title-1"]')?.textContent?.trim()).toBe(
        'Booked',
      );
      expect(compiled.textContent).toContain("Someone else's booking: details are private.");
      expect(compiled.querySelector('[data-testid="edit-booking-1"]')).toBeNull();
      expect(compiled.querySelector('[data-testid="delete-booking-1"]')).toBeNull();

      expect(compiled.querySelector('[data-testid="booking-title-2"]')?.textContent?.trim()).toBe(
        'My meeting',
      );
      expect(compiled.querySelector('[data-testid="edit-booking-2"]')).not.toBeNull();
      expect(compiled.querySelector('[data-testid="delete-booking-2"]')).not.toBeNull();
    });

    it('never shows a late answer for the organisation the user switched away from', async () => {
      const acmeBookings = new Subject<Booking[]>();
      bookingsFor = (scope) =>
        scope?.organizationId === 'org-acme'
          ? acmeBookings
          : of([{ ...booking, id: 9, title: 'Zeta meeting', canModify: true }]);

      const { fixture, compiled } = await render();

      const select = compiled.querySelector('#organization') as HTMLSelectElement;
      select.value = 'org-zeta';
      select.dispatchEvent(new Event('change'));
      fixture.detectChanges();

      // Acme's answer arrives after the switch.
      acmeBookings.next([{ ...booking, id: 8, title: 'Acme secret' }]);
      acmeBookings.complete();
      fixture.detectChanges();

      expect(compiled.textContent).toContain('Zeta meeting');
      expect(compiled.textContent).not.toContain('Acme secret');
      expect(bookingApi.getBookings).toHaveBeenLastCalledWith({ organizationId: 'org-zeta' });
    });
  });
});

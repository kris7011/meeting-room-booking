import { DatePipe } from '@angular/common';
import { HttpErrorResponse } from '@angular/common/http';
import { Component, OnInit, computed, inject, signal } from '@angular/core';
import {
  AbstractControl,
  FormBuilder,
  ReactiveFormsModule,
  ValidationErrors,
  Validators,
} from '@angular/forms';
import { finalize, forkJoin, map, of, switchMap } from 'rxjs';
import { AuthService, SignInRequiredError } from './core/auth/auth.service';
import { Organization } from './core/models/account.model';
import { Booking } from './core/models/booking.model';
import { MeetingRoom } from './core/models/meeting-room.model';
import { SaveBookingRequest } from './core/models/save-booking-request.model';
import { AccountApiService } from './core/services/account-api.service';
import { BookingApiService, BookingScope } from './core/services/booking-api.service';

interface ApiProblem {
  readonly code?: string;
  readonly detail?: string;
  readonly errors?: Record<string, string[]>;
}

/** Messages for the API's stable problem codes; its English `detail` is the fallback. */
const KNOWN_PROBLEMS: Record<string, string> = {
  booking_overlap: 'The selected room is already booked during that time period.',
  booking_permission_denied: 'Members can only change and delete bookings they made themselves.',
  organization_permission_denied: 'Your role in this organisation does not allow this.',
  organization_not_found:
    'You are no longer a member of this organisation. Refresh to see your organisations.',
  user_disabled: 'Your Room Booking account has been disabled.',
  user_not_registered:
    'Your Microsoft account is not registered with Room Booking yet. Refresh to register.',
  authentication_not_configured:
    'The API is not configured for Microsoft sign-in (Entra:ApiClientId is not set).',
};

function validTimeRangeValidator(control: AbstractControl): ValidationErrors | null {
  const startLocal = control.get('startLocal')?.value;
  const endLocal = control.get('endLocal')?.value;

  if (
    typeof startLocal !== 'string' ||
    typeof endLocal !== 'string' ||
    startLocal.length === 0 ||
    endLocal.length === 0
  ) {
    return null;
  }

  const startTime = new Date(startLocal).getTime();
  const endTime = new Date(endLocal).getTime();

  if (Number.isNaN(startTime) || Number.isNaN(endTime)) {
    return {
      invalidDateTime: true,
    };
  }

  if (endTime <= startTime) {
    return {
      invalidTimeRange: true,
    };
  }

  return null;
}

@Component({
  selector: 'app-root',
  imports: [DatePipe, ReactiveFormsModule],
  templateUrl: './app.html',
  styleUrl: './app.css',
})
/**
 * Signed out (or without sign-in configured), the app uses the original
 * endpoints, which only the API's Development-only anonymous demo mode
 * answers. Signed in, it uses the endpoints of the chosen organisation; the
 * API checks membership and role on every request, and the buttons shown
 * here only follow what it says (`canModify`).
 */
export class App implements OnInit {
  private readonly bookingApiService = inject(BookingApiService);
  private readonly accountApiService = inject(AccountApiService);
  private readonly formBuilder = inject(FormBuilder);
  protected readonly auth = inject(AuthService);

  protected readonly organizations = signal<Organization[]>([]);
  protected readonly activeOrganizationId = signal<string | null>(null);
  protected readonly activeOrganization = computed(
    () =>
      this.organizations().find(
        (organization) => organization.publicId === this.activeOrganizationId(),
      ) ?? null,
  );
  /** Shown instead of the schedule, e.g. when the user belongs to no organisation. */
  protected readonly accountMessage = signal<string | null>(null);

  /** Bumped by every load and organisation switch, so a late response is ignored. */
  private loadGeneration = 0;

  protected readonly rooms = signal<MeetingRoom[]>([]);
  protected readonly bookings = signal<Booking[]>([]);

  protected readonly isLoading = signal(true);
  protected readonly isSaving = signal(false);
  protected readonly deletingBookingId = signal<number | null>(null);
  protected readonly pendingDeleteBookingId = signal<number | null>(null);
  protected readonly editingBookingId = signal<number | null>(null);

  protected readonly errorMessage = signal<string | null>(null);
  protected readonly successMessage = signal<string | null>(null);

  protected readonly isEditing = computed(() => this.editingBookingId() !== null);

  protected readonly formHeading = computed(() =>
    this.isEditing() ? 'Edit booking' : 'Create a booking',
  );

  protected readonly formDescription = computed(() =>
    this.isEditing()
      ? 'Update the selected booking. The overlap rule is checked again when you save.'
      : 'Select a room and time period. The API prevents overlapping bookings.',
  );

  protected readonly bookingForm = this.formBuilder.nonNullable.group(
    {
      roomId: [0, [Validators.required, Validators.min(1)]],
      title: ['', [Validators.required, Validators.maxLength(200)]],
      bookedBy: ['', [Validators.required, Validators.maxLength(120)]],
      startLocal: ['', Validators.required],
      endLocal: ['', Validators.required],
    },
    {
      validators: validTimeRangeValidator,
    },
  );

  public ngOnInit(): void {
    this.resetFormValues();
    this.refresh();
  }

  protected refresh(): void {
    if (this.auth.status() === 'signed-in') {
      this.loadAccount();
    } else {
      this.loadData();
    }
  }

  protected signIn(): void {
    void this.auth.signIn();
  }

  protected signOut(): void {
    this.loadGeneration++;
    this.organizations.set([]);
    this.activeOrganizationId.set(null);
    this.rooms.set([]);
    this.bookings.set([]);
    void this.auth.signOut();
  }

  /**
   * Who the API says the user is, registering them on their first visit
   * (idempotent), then their organisations. Registration grants no access:
   * a new user belongs to no organisation until they create one or accept
   * an invitation.
   */
  protected loadAccount(): void {
    const generation = ++this.loadGeneration;

    this.isLoading.set(true);
    this.errorMessage.set(null);
    this.accountMessage.set(null);

    this.accountApiService
      .getCurrentUser()
      .pipe(
        switchMap((current) =>
          current.user !== null
            ? of(current)
            : this.accountApiService
                .register()
                .pipe(map((registration) => registration.currentUser)),
        ),
        switchMap(() => this.accountApiService.getOrganizations()),
      )
      .subscribe({
        next: (organizations) => {
          if (generation !== this.loadGeneration) {
            return;
          }

          this.organizations.set(organizations);

          const active =
            organizations.find(
              (organization) => organization.publicId === this.activeOrganizationId(),
            ) ??
            organizations[0] ??
            null;

          this.activeOrganizationId.set(active?.publicId ?? null);

          if (active === null) {
            this.isLoading.set(false);
            this.rooms.set([]);
            this.bookings.set([]);
            this.accountMessage.set(
              'You are not a member of an organisation yet. Create one or accept an invitation in the Room Booking mobile app; it then appears here.',
            );
            return;
          }

          this.loadData();
        },
        error: (error: unknown) => {
          if (generation !== this.loadGeneration) {
            return;
          }

          this.isLoading.set(false);
          this.errorMessage.set(this.getErrorMessage(error, 'Your account could not be loaded.'));
        },
      });
  }

  protected selectOrganization(organizationId: string): void {
    if (organizationId === this.activeOrganizationId()) {
      return;
    }

    // Nothing of the previous organisation stays on screen.
    this.activeOrganizationId.set(organizationId);
    this.rooms.set([]);
    this.bookings.set([]);
    this.successMessage.set(null);
    this.resetFormValues();
    this.loadData();
  }

  protected displayTitle(booking: Booking): string {
    return booking.detailsHidden || booking.title === null ? 'Booked' : booking.title;
  }

  /** Organisation endpoints say it; the original endpoints (demo mode) allow every change. */
  protected canModify(booking: Booking): boolean {
    return booking.canModify !== false;
  }

  protected loadData(): void {
    const generation = ++this.loadGeneration;
    const scope = this.currentScope();

    this.errorMessage.set(null);
    this.successMessage.set(null);

    if (this.auth.status() === 'signed-in' && scope === null) {
      // Signed in without an organisation: there is nothing to show.
      this.isLoading.set(false);
      return;
    }

    this.isLoading.set(true);

    forkJoin({
      rooms: this.bookingApiService.getRooms(scope),
      bookings: this.bookingApiService.getBookings(scope),
    })
      .pipe(
        finalize(() => {
          if (generation === this.loadGeneration) {
            this.isLoading.set(false);
          }
        }),
      )
      .subscribe({
        next: ({ rooms, bookings }) => {
          if (generation !== this.loadGeneration) {
            return;
          }

          this.rooms.set(rooms);
          this.bookings.set(this.sortBookings(bookings));

          if (this.bookingForm.controls.roomId.value === 0 && rooms.length > 0) {
            this.bookingForm.controls.roomId.setValue(rooms[0].id);
          }
        },
        error: (error: unknown) => {
          if (generation !== this.loadGeneration) {
            return;
          }

          this.errorMessage.set(
            this.getErrorMessage(
              error,
              'Rooms and bookings could not be loaded. Please try again.',
            ),
          );
        },
      });
  }

  protected saveBooking(): void {
    this.errorMessage.set(null);
    this.successMessage.set(null);

    if (this.bookingForm.invalid) {
      this.bookingForm.markAllAsTouched();

      return;
    }

    const formValue = this.bookingForm.getRawValue();

    const request: SaveBookingRequest = {
      roomId: formValue.roomId,
      title: formValue.title.trim(),
      bookedBy: formValue.bookedBy.trim(),
      startUtc: new Date(formValue.startLocal).toISOString(),
      endUtc: new Date(formValue.endLocal).toISOString(),
    };

    const editingBookingId = this.editingBookingId();
    const scope = this.currentScope();
    const organizationId = this.activeOrganizationId();

    const operation =
      editingBookingId === null
        ? this.bookingApiService.createBooking(request, scope)
        : this.bookingApiService.updateBooking(editingBookingId, request, scope);

    this.isSaving.set(true);

    operation
      .pipe(
        finalize(() => {
          this.isSaving.set(false);
        }),
      )
      .subscribe({
        next: (savedBooking) => {
          if (organizationId !== this.activeOrganizationId()) {
            // The user switched organisation while it was being saved.
            return;
          }

          if (editingBookingId === null) {
            this.bookings.update((bookings) => this.sortBookings([...bookings, savedBooking]));
          } else {
            this.bookings.update((bookings) =>
              this.sortBookings(
                bookings.map((booking) =>
                  booking.id === savedBooking.id ? savedBooking : booking,
                ),
              ),
            );
          }

          this.resetFormValues();

          this.successMessage.set(
            editingBookingId === null ? 'The booking was created.' : 'The booking was updated.',
          );
        },
        error: (error: unknown) => {
          this.errorMessage.set(this.getErrorMessage(error, 'The booking could not be saved.'));
        },
      });
  }

  protected editBooking(booking: Booking): void {
    this.editingBookingId.set(booking.id);
    this.pendingDeleteBookingId.set(null);
    this.errorMessage.set(null);
    this.successMessage.set(null);

    this.bookingForm.reset({
      roomId: booking.roomId,
      title: booking.title ?? '',
      bookedBy: booking.bookedBy ?? '',
      startLocal: this.toDateTimeLocalValue(booking.startUtc),
      endLocal: this.toDateTimeLocalValue(booking.endUtc),
    });
  }

  protected cancelEditing(): void {
    this.errorMessage.set(null);
    this.successMessage.set(null);
    this.resetFormValues();
  }

  protected requestDelete(bookingId: number): void {
    this.pendingDeleteBookingId.set(bookingId);
    this.errorMessage.set(null);
    this.successMessage.set(null);
  }

  protected cancelDelete(): void {
    this.pendingDeleteBookingId.set(null);
  }

  protected deleteBooking(booking: Booking): void {
    this.deletingBookingId.set(booking.id);
    this.errorMessage.set(null);
    this.successMessage.set(null);

    const organizationId = this.activeOrganizationId();

    this.bookingApiService
      .deleteBooking(booking.id, this.currentScope())
      .pipe(
        finalize(() => {
          this.deletingBookingId.set(null);
        }),
      )
      .subscribe({
        next: () => {
          if (organizationId !== this.activeOrganizationId()) {
            return;
          }

          this.bookings.update((bookings) => bookings.filter((item) => item.id !== booking.id));

          this.pendingDeleteBookingId.set(null);

          if (this.editingBookingId() === booking.id) {
            this.resetFormValues();
          }

          this.successMessage.set(`"${this.displayTitle(booking)}" was deleted.`);
        },
        error: (error: unknown) => {
          this.errorMessage.set(this.getErrorMessage(error, 'The booking could not be deleted.'));
        },
      });
  }

  private currentScope(): BookingScope {
    const organizationId = this.activeOrganizationId();

    return this.auth.status() === 'signed-in' && organizationId !== null
      ? { organizationId }
      : null;
  }

  private resetFormValues(): void {
    const defaultTimeRange = this.createDefaultTimeRange();

    this.editingBookingId.set(null);
    this.pendingDeleteBookingId.set(null);

    this.bookingForm.reset({
      roomId: this.rooms()[0]?.id ?? 0,
      title: '',
      bookedBy: '',
      startLocal: defaultTimeRange.startLocal,
      endLocal: defaultTimeRange.endLocal,
    });
  }

  private createDefaultTimeRange(): {
    readonly startLocal: string;
    readonly endLocal: string;
  } {
    const start = new Date();

    start.setSeconds(0, 0);
    start.setMinutes(0);
    start.setHours(start.getHours() + 1);

    const end = new Date(start.getTime() + 60 * 60 * 1000);

    return {
      startLocal: this.toDateTimeLocalValue(start),
      endLocal: this.toDateTimeLocalValue(end),
    };
  }

  private toDateTimeLocalValue(value: string | Date): string {
    const date = typeof value === 'string' ? new Date(value) : value;

    const localDate = new Date(date.getTime() - date.getTimezoneOffset() * 60_000);

    return localDate.toISOString().slice(0, 16);
  }

  private sortBookings(bookings: readonly Booking[]): Booking[] {
    return [...bookings].sort(
      (first, second) => new Date(first.startUtc).getTime() - new Date(second.startUtc).getTime(),
    );
  }

  private getErrorMessage(error: unknown, fallbackMessage: string): string {
    if (error instanceof SignInRequiredError) {
      return 'Your Microsoft sign-in has expired. Sign in again.';
    }

    if (!(error instanceof HttpErrorResponse)) {
      return fallbackMessage;
    }

    if (error.status === 0) {
      return 'The booking API could not be reached. Check that the .NET API is running.';
    }

    if (error.status === 401) {
      return this.auth.status() === 'signed-in'
        ? 'The API did not accept your sign-in. Sign out and sign in again.'
        : 'The API requires sign-in. Sign in with Microsoft, or start the API in its Development demo mode (LegacyApi__AllowAnonymous=true).';
    }

    if (typeof error.error !== 'object' || error.error === null) {
      return fallbackMessage;
    }

    const problem = error.error as ApiProblem;
    const knownMessage = problem.code === undefined ? undefined : KNOWN_PROBLEMS[problem.code];

    if (knownMessage !== undefined) {
      return knownMessage;
    }

    if (problem.errors !== undefined) {
      const firstValidationMessage = Object.values(problem.errors).flat().at(0);

      if (firstValidationMessage !== undefined) {
        return firstValidationMessage;
      }
    }

    return problem.detail ?? fallbackMessage;
  }
}

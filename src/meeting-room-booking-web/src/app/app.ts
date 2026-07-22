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
import { finalize, forkJoin } from 'rxjs';
import { Booking } from './core/models/booking.model';
import { MeetingRoom } from './core/models/meeting-room.model';
import { SaveBookingRequest } from './core/models/save-booking-request.model';
import { BookingApiService } from './core/services/booking-api.service';

interface ApiProblem {
  readonly code?: string;
  readonly detail?: string;
  readonly errors?: Record<string, string[]>;
}

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
export class App implements OnInit {
  private readonly bookingApiService = inject(BookingApiService);
  private readonly formBuilder = inject(FormBuilder);

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
    this.loadData();
  }

  protected loadData(): void {
    this.isLoading.set(true);
    this.errorMessage.set(null);
    this.successMessage.set(null);

    forkJoin({
      rooms: this.bookingApiService.getRooms(),
      bookings: this.bookingApiService.getBookings(),
    })
      .pipe(
        finalize(() => {
          this.isLoading.set(false);
        }),
      )
      .subscribe({
        next: ({ rooms, bookings }) => {
          this.rooms.set(rooms);
          this.bookings.set(this.sortBookings(bookings));

          if (this.bookingForm.controls.roomId.value === 0 && rooms.length > 0) {
            this.bookingForm.controls.roomId.setValue(rooms[0].id);
          }
        },
        error: (error: unknown) => {
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

    const operation =
      editingBookingId === null
        ? this.bookingApiService.createBooking(request)
        : this.bookingApiService.updateBooking(editingBookingId, request);

    this.isSaving.set(true);

    operation
      .pipe(
        finalize(() => {
          this.isSaving.set(false);
        }),
      )
      .subscribe({
        next: (savedBooking) => {
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
      title: booking.title,
      bookedBy: booking.bookedBy,
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

    this.bookingApiService
      .deleteBooking(booking.id)
      .pipe(
        finalize(() => {
          this.deletingBookingId.set(null);
        }),
      )
      .subscribe({
        next: () => {
          this.bookings.update((bookings) => bookings.filter((item) => item.id !== booking.id));

          this.pendingDeleteBookingId.set(null);

          if (this.editingBookingId() === booking.id) {
            this.resetFormValues();
          }

          this.successMessage.set(`"${booking.title}" was deleted.`);
        },
        error: (error: unknown) => {
          this.errorMessage.set(this.getErrorMessage(error, 'The booking could not be deleted.'));
        },
      });
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
    if (!(error instanceof HttpErrorResponse)) {
      return fallbackMessage;
    }

    if (error.status === 0) {
      return 'The booking API could not be reached. Check that the .NET API is running.';
    }

    if (typeof error.error !== 'object' || error.error === null) {
      return fallbackMessage;
    }

    const problem = error.error as ApiProblem;

    if (problem.code === 'booking_overlap') {
      return 'The selected room is already booked during that time period.';
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

import { MIN_TOAST_DURATION_MS, ToastMessage, ToastService } from './toast.service';

describe('ToastService', () => {
  let service: ToastService;
  let current: ToastMessage[];

  beforeEach(() => {
    jest.useFakeTimers();
    service = new ToastService();
    service.messages$.subscribe((messages) => (current = messages));
  });

  afterEach(() => {
    jest.clearAllTimers();
    jest.useRealTimers();
  });

  it('keeps a toast for at least five seconds by default', () => {
    expect(MIN_TOAST_DURATION_MS).toBe(5000);

    service.success('Saved');

    jest.advanceTimersByTime(4999);
    expect(current.map((t) => t.message)).toEqual(['Saved']);

    jest.advanceTimersByTime(1);
    expect(current).toEqual([]);
  });

  it.each([
    ['error', 'Error'],
    ['warning', 'Warning'],
    ['info', 'Info']
  ] as const)('gives %s toasts the same five-second default', (method, title) => {
    service[method]('Message');
    expect(current[0]).toEqual(expect.objectContaining({ type: method, title, message: 'Message' }));

    jest.advanceTimersByTime(4999);
    expect(current).toHaveLength(1);
    jest.advanceTimersByTime(1);
    expect(current).toEqual([]);
  });

  it.each([1000, 0, -50])('raises a shorter explicit duration (%d ms) to five seconds', (duration) => {
    service.show('info', 'Short', 'Info', duration);

    jest.advanceTimersByTime(4999);
    expect(current).toHaveLength(1);

    jest.advanceTimersByTime(1);
    expect(current).toEqual([]);
  });

  it('keeps a longer explicit duration unchanged', () => {
    service.show('info', 'Long', 'Info', 12000);

    jest.advanceTimersByTime(11999);
    expect(current).toHaveLength(1);

    jest.advanceTimersByTime(1);
    expect(current).toEqual([]);
  });

  it('removes only the dismissed toast immediately and cancels its timer', () => {
    service.success('First');
    service.error('Second');
    const firstId = current[0].id;
    const emissions: ToastMessage[][] = [];
    service.messages$.subscribe((messages) => emissions.push(messages));
    emissions.length = 0;

    service.remove(firstId);

    expect(current.map((t) => t.message)).toEqual(['Second']);
    expect(jest.getTimerCount()).toBe(1);

    jest.advanceTimersByTime(5000);
    // One emission for the dismissal and one for the second toast's own expiry; the first toast's
    // cancelled timer adds nothing.
    expect(emissions.map((list) => list.map((t) => t.message))).toEqual([['Second'], []]);
  });

  it('ignores removing an id that is already gone', () => {
    service.success('Only');
    const id = current[0].id;
    service.remove(id);

    expect(() => service.remove(id)).not.toThrow();
    expect(() => service.remove('toast-unknown')).not.toThrow();
    expect(current).toEqual([]);
  });

  it('lets stacked toasts with different durations expire independently', () => {
    service.show('success', 'Short', 'Success', 5000);
    service.show('warning', 'Long', 'Warning', 8000);

    jest.advanceTimersByTime(5000);
    expect(current.map((t) => t.message)).toEqual(['Long']);

    jest.advanceTimersByTime(3000);
    expect(current).toEqual([]);
  });
});

import { useState, useCallback, useRef } from 'react';
import { KeystrokeEvent } from '../types';

interface UseKeystrokeCaptureReturn {
  events: KeystrokeEvent[];
  startCapture: () => void;
  stopCapture: () => void;
  clearEvents: () => void;
  isCapturing: boolean;
  handleKeyDown: (e: React.KeyboardEvent) => void;
  handleKeyUp: (e: React.KeyboardEvent) => void;
}

export const useKeystrokeCapture = (): UseKeystrokeCaptureReturn => {
  const [events, setEvents] = useState<KeystrokeEvent[]>([]);
  const [isCapturing, setIsCapturing] = useState(false);
  const startTimeRef = useRef<number>(0);

  const startCapture = useCallback(() => {
    setEvents([]);
    startTimeRef.current = Date.now();
    setIsCapturing(true);
  }, []);

  const stopCapture = useCallback(() => {
    setIsCapturing(false);
  }, []);

  const clearEvents = useCallback(() => {
    setEvents([]);
  }, []);

  const handleKeyDown = useCallback((e: React.KeyboardEvent) => {
    if (!isCapturing) return;

    const event: KeystrokeEvent = {
      key: e.key,
      timestamp: Date.now() - startTimeRef.current,
      type: 'keydown',
    };
    setEvents((prev) => [...prev, event]);
  }, [isCapturing]);

  const handleKeyUp = useCallback((e: React.KeyboardEvent) => {
    if (!isCapturing) return;

    const event: KeystrokeEvent = {
      key: e.key,
      timestamp: Date.now() - startTimeRef.current,
      type: 'keyup',
    };
    setEvents((prev) => [...prev, event]);
  }, [isCapturing]);

  return {
    events,
    startCapture,
    stopCapture,
    clearEvents,
    isCapturing,
    handleKeyDown,
    handleKeyUp,
  };
};

import React, { useState, useEffect, useRef, useCallback } from 'react';
import { v4 as uuidv4 } from 'uuid';
import { useAuth } from '../context/AuthContext';
import { useKeystrokeCapture } from '../hooks/useKeystrokeCapture';
import { profilerService } from '../services/api';
import { IdentifyResponse } from '../types';
import { getRandomSentence } from '../data/sentences';
import './TypingTest.css';

const TEST_DURATION = 15;

const TypingTest: React.FC = () => {
  const { isAuthenticated, isIncognito } = useAuth();
  const {
    events,
    startCapture,
    stopCapture,
    clearEvents,
    handleKeyDown,
    handleKeyUp,
  } = useKeystrokeCapture();

  const [currentSentence, setCurrentSentence] = useState('');
  const [userInput, setUserInput] = useState('');
  const [timeLeft, setTimeLeft] = useState(TEST_DURATION);
  const [isRunning, setIsRunning] = useState(false);
  const [isReady, setIsReady] = useState(true);
  const [showResults, setShowResults] = useState(false);
  const [identifyResult, setIdentifyResult] = useState<IdentifyResponse | null>(null);
  const [currentPrediction, setCurrentPrediction] = useState<IdentifyResponse | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState('');
  const [segmentCount, setSegmentCount] = useState(0);
  const [waitingForInput, setWaitingForInput] = useState(false);

  const [testCount, setTestCount] = useState(() => {
    const saved = localStorage.getItem('user_test_count');
    return saved ? parseInt(saved, 10) : 0;
  });

  const [sessionId, setSessionId] = useState<string>('');

  const inputRef = useRef<HTMLInputElement>(null);
  const sentenceRef = useRef<HTMLDivElement>(null);
  const currentCharRef = useRef<HTMLSpanElement>(null);
  const hasStartedRef = useRef(false);
  const eventsRef = useRef(events);
  const isSubmittingRef = useRef(false);
  const timerIdRef = useRef<NodeJS.Timeout | null>(null);

  useEffect(() => {
    localStorage.setItem('user_test_count', testCount.toString());
  }, [testCount]);

  useEffect(() => {
    eventsRef.current = events;
  }, [events]);

  useEffect(() => {
      setSessionId(uuidv4());
  }, []);

  const resetTest = useCallback((fullReset = true) => {
    setCurrentSentence(getRandomSentence());
    setUserInput('');
    setTimeLeft(TEST_DURATION);

    isSubmittingRef.current = false;

    setIsRunning(false);
    setIsReady(true);
    setShowResults(false);
    setIdentifyResult(null);
    setCurrentPrediction(null);
    setError('');
    setSegmentCount(0);
    setWaitingForInput(false);
    hasStartedRef.current = false;

    if (fullReset) {
        setSessionId(uuidv4());
    } else {
        setIsRunning(true);
        startCapture();
    }

    clearEvents();
    if (sentenceRef.current) {
      sentenceRef.current.scrollTop = 0;
    }
    setTimeout(() => inputRef.current?.focus(), 0);
  }, [clearEvents, startCapture]);

  const startTest = useCallback(() => {
    if (hasStartedRef.current) return;
    hasStartedRef.current = true;
    setIsRunning(true);
    setIsReady(false);
    startCapture();
  }, [startCapture]);

  // Function to start the next segment (called when user starts typing after segment ends)
  const startNextSegment = useCallback(() => {
    setWaitingForInput(false);
    setIsRunning(true);
    startCapture();

    // Start the timer
    if (timerIdRef.current) {
      clearInterval(timerIdRef.current);
    }
    timerIdRef.current = setInterval(() => {
      setTimeLeft((prev) => {
        if (prev <= 1) {
          if (timerIdRef.current) {
            clearInterval(timerIdRef.current);
            timerIdRef.current = null;
          }
          return 0;
        }
        return prev - 1;
      });
    }, 1000);
  }, [startCapture]);

  // Timer countdown function
  const startTimer = useCallback(() => {
    // Clear any existing timer
    if (timerIdRef.current) {
      clearInterval(timerIdRef.current);
    }

    timerIdRef.current = setInterval(() => {
      setTimeLeft((prev) => {
        if (prev <= 1) {
          if (timerIdRef.current) {
            clearInterval(timerIdRef.current);
            timerIdRef.current = null;
          }
          return 0;
        }
        return prev - 1;
      });
    }, 1000);
  }, []);

  const endTest = useCallback(async () => {
    if (isSubmittingRef.current) return;
    isSubmittingRef.current = true;

    // Clear the timer
    if (timerIdRef.current) {
      clearInterval(timerIdRef.current);
      timerIdRef.current = null;
    }

    stopCapture();
    const currentEvents = eventsRef.current;

    if (currentEvents.length < 5) {
      setIsRunning(false);
      setShowResults(true);
      setError('Too few keystrokes. Please try again.');
      isSubmittingRef.current = false;
      return;
    }

    setIsSubmitting(true);

    try {
      const sessionData = {
        platform: 'web',
        events: currentEvents,
        sessionId: sessionId
      };

      if (isAuthenticated) {
        setIsRunning(false);
        setShowResults(true);
        await profilerService.submitSession(sessionData);
        setTestCount((prev) => prev + 1);
      } else if (isIncognito) {
        const result = await profilerService.identifyUser(sessionData);

        // Always update the current prediction to show below text
        setCurrentPrediction(result);
        setSegmentCount(prev => prev + 1);

        if (result.status === 'Continue') {
            // Continue gathering data - reset with new sentence but wait for user input
            setCurrentSentence(getRandomSentence());
            setUserInput('');
            setTimeLeft(TEST_DURATION);
            clearEvents();
            // Don't start capture or timer yet - wait for user to start typing
            setIsRunning(false);
            setWaitingForInput(true);
            hasStartedRef.current = false;
            isSubmittingRef.current = false;
            setIsSubmitting(false);
            if (sentenceRef.current) {
              sentenceRef.current.scrollTop = 0;
            }
            setTimeout(() => inputRef.current?.focus(), 0);
        } else {
            // Show the identification result
            setIsRunning(false);
            setShowResults(true);
            setIdentifyResult(result);
        }
      } else {
        // Guest mode - reset after 15 seconds with new text
        setIsRunning(false);
        setShowResults(true);
        // Auto-reset after showing results briefly
        setTimeout(() => {
          resetTest(true);
        }, 3000);
      }
    } catch (err: any) {
      setIsRunning(false);
      setShowResults(true);
      setError(err.response?.data?.message || 'Error processing data');
    } finally {
      if (!isIncognito || showResults) {
        setIsSubmitting(false);
      }
      if (!isIncognito && !isAuthenticated) {
          isSubmittingRef.current = false;
      }
    }
  }, [isAuthenticated, isIncognito, stopCapture, sessionId, resetTest, clearEvents, showResults]);

  const endTestRef = useRef(endTest);
  useEffect(() => {
    endTestRef.current = endTest;
  }, [endTest]);

  // Effect to call endTest when timer reaches 0
  useEffect(() => {
    if (timeLeft === 0 && isRunning) {
      endTestRef.current();
    }
  }, [timeLeft, isRunning]);

  // Effect to start timer when test starts running
  useEffect(() => {
    if (isRunning && timeLeft > 0 && !timerIdRef.current) {
      startTimer();
    }

    return () => {
      if (timerIdRef.current) {
        clearInterval(timerIdRef.current);
        timerIdRef.current = null;
      }
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isRunning, startTimer]);

  useEffect(() => {
    setCurrentSentence(getRandomSentence());
    inputRef.current?.focus();
  }, [isIncognito]);

  useEffect(() => {
    if (currentCharRef.current && sentenceRef.current) {
      const container = sentenceRef.current;
      const currentChar = currentCharRef.current;
      const containerRect = container.getBoundingClientRect();
      const charRect = currentChar.getBoundingClientRect();

      if (charRect.top > containerRect.top + containerRect.height * 0.6) {
        container.scrollTop += charRect.top - containerRect.top - containerRect.height * 0.3;
      }
    }
  }, [userInput]);

  // Reset test when mode changes
  useEffect(() => {
    resetTest(true);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [isIncognito, isAuthenticated]);

  const handleInputChange = (e: React.ChangeEvent<HTMLInputElement>) => {
    if (showResults || isSubmitting) return;

    // If waiting for input in incognito mode, start next segment
    if (waitingForInput && isIncognito) {
      startNextSegment();
    } else if (isReady && !isRunning) {
      startTest();
    }

    setUserInput(e.target.value);
  };

  const handleKeyDownWrapper = (e: React.KeyboardEvent) => {
    if (showResults || isSubmitting) return;

    // If waiting for input in incognito mode, start next segment
    if (waitingForInput && isIncognito && e.key.length === 1) {
      startNextSegment();
    } else if (isReady && !isRunning && e.key.length === 1) {
      startTest();
    }

    handleKeyDown(e);
  };

  const handleKeyUpWrapper = (e: React.KeyboardEvent) => {
    if (showResults || isSubmitting) return;
    handleKeyUp(e);
  };

  const handleContainerClick = () => {
    inputRef.current?.focus();
  };

  const renderSentence = () => {
    return currentSentence.split('').map((char, index) => {
      let className = 'char';
      const isCurrent = index === userInput.length;
      if (index < userInput.length) {
        className += userInput[index] === char ? ' correct' : ' incorrect';
      } else if (isCurrent) {
        className += ' current';
      }
      return (
        <span
          key={index}
          className={className}
          ref={isCurrent ? currentCharRef : null}
        >
          {char}
        </span>
      );
    });
  };

  const calculateAccuracy = () => {
    if (userInput.length === 0) return 0;
    let correct = 0;
    for (let i = 0; i < userInput.length; i++) {
      if (userInput[i] === currentSentence[i]) {
        correct++;
      }
    }
    return Math.round((correct / userInput.length) * 100);
  };

  const calculateWPM = () => {
    const wordsTyped = userInput.trim().split(/\s+/).length;
    const timeSpent = TEST_DURATION - timeLeft;
    if (timeSpent === 0) return 0;
    return Math.round((wordsTyped / timeSpent) * 60);
  };

  return (
    <div className="typing-test" onClick={handleContainerClick}>
      <div className="test-header">
        <div className="timer">
          <span className={timeLeft <= 5 && isRunning ? 'warning' : ''}>
            {timeLeft}s
          </span>
        </div>
        <div className="mode-indicator">
          {isAuthenticated && <span className="mode training">Training Mode</span>}
          {isIncognito && <span className="mode incognito">Incognito Mode</span>}
          {!isAuthenticated && !isIncognito && (
            <span className="mode guest">Guest Mode - Sign In</span>
          )}
        </div>
      </div>

      <div className="sentence-display" ref={sentenceRef}>
        {renderSentence()}
      </div>

      <input
        ref={inputRef}
        type="text"
        value={userInput}
        onChange={handleInputChange}
        onKeyDown={handleKeyDownWrapper}
        onKeyUp={handleKeyUpWrapper}
        className="typing-input"
        disabled={showResults || isSubmitting}
        autoComplete="off"
        autoCapitalize="off"
        autoCorrect="off"
        spellCheck={false}
        autoFocus
      />

      {isReady && !showResults && !waitingForInput && (
        <div className="start-hint">
          Start typing to begin the test...
        </div>
      )}

      {waitingForInput && isIncognito && (
        <div className="start-hint">
          Start typing to continue analysis...
        </div>
      )}

      {isSubmitting && isIncognito && (
         <div className="start-hint processing">Processing segment...</div>
      )}

      {/* Incognito mode prediction box - shown while typing */}
      {isIncognito && currentPrediction && !showResults && (
        <div className="prediction-box">
          <div className="prediction-header">
            <span className="segment-indicator">Segment {segmentCount}</span>
          </div>
          {currentPrediction.user !== 'Unknown' ? (
            <p className="prediction-text">
              You are <strong>@{currentPrediction.user}</strong> - probability: <strong>{currentPrediction.confidence.toFixed(1)}%</strong>
            </p>
          ) : (
            <p className="prediction-text unknown">
              User not recognized - probability: <strong>{currentPrediction.confidence.toFixed(1)}%</strong>
            </p>
          )}
        </div>
      )}

      {showResults && (
        <div className="results">
          <h3>Test Complete!</h3>
          <div className="stats">
            <div className="stat">
              <span className="stat-value">{calculateWPM()}</span>
              <span className="stat-label">WPM</span>
            </div>
            <div className="stat">
              <span className="stat-value">{calculateAccuracy()}%</span>
              <span className="stat-label">Accuracy</span>
            </div>
            <div className="stat">
              <span className="stat-value">{eventsRef.current.length}</span>
              <span className="stat-label">Keystrokes</span>
            </div>
          </div>

          {error && <p className="error">{error}</p>}

          {isAuthenticated && !error && !isSubmitting && (
            <p className="success">
              Session #{testCount} saved! Learning your typing pattern.
            </p>
          )}

          {!isAuthenticated && !isIncognito && !error && (
            <p className="guest-message">
              Sign in to save your typing patterns. Restarting in 3 seconds...
            </p>
          )}

          {identifyResult && (
            <div className="identify-result">
              {identifyResult.user !== 'Unknown' ? (
                <>
                  <p className="identified">
                    You are <strong>@{identifyResult.user}</strong>
                  </p>
                  <p className="confidence">
                    Probability: {identifyResult.confidence.toFixed(1)}%
                  </p>
                </>
              ) : (
                <>
                  <p className="not-identified">User not recognized</p>
                  <p className="confidence">
                    Probability: {identifyResult.confidence.toFixed(1)}%
                  </p>
                </>
              )}
            </div>
          )}

          <button className="retry-btn" onClick={() => resetTest(true)}>
            Try Again
          </button>
        </div>
      )}

      <div className="instructions">
        <p>
          {isAuthenticated
            ? 'Type the sentence above. Your typing pattern will be saved for training.'
            : isIncognito
            ? 'Type the sentence above. Your pattern will be analyzed after 15 seconds to identify you.'
            : 'Sign in to train the model, or go incognito to test identification.'}
        </p>
      </div>
    </div>
  );
};

export default TypingTest;

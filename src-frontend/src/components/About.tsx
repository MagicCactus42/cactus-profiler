import React from 'react';
import './About.css';

const About: React.FC = () => {
  return (
    <div className="about-container">
      <div className="about-content">
        <h1 className="about-title">About This Project</h1>

        <section className="about-section">
          <h2>What is Cactus-Profiler?</h2>
          <p>
            Cactus-Profiler is a keystroke biometrics application that identifies users based on their
            unique typing patterns. Every person has a distinctive way of typing - the rhythm,
            speed, and timing between keystrokes create a "typing fingerprint" that can be used
            to identify them.
          </p>
        </section>

        <section className="about-section">
          <h2>How It Works</h2>
          <p>
            The system captures various typing metrics including:
          </p>
          <ul>
            <li><strong>Dwell time</strong> - how long each key is held down</li>
            <li><strong>Flight time</strong> - the time between releasing one key and pressing the next</li>
            <li><strong>Typing speed</strong> - words per minute and characters per second</li>
            <li><strong>Rhythm patterns</strong> - the consistency and variation in typing cadence</li>
          </ul>
          <p>
            These features are processed by a machine learning model that learns to recognize
            individual typing patterns and can identify users with high accuracy.
          </p>
        </section>

        <section className="about-section">
          <h2>Progressive Identification</h2>
          <p>
            The incognito mode uses a progressive elimination algorithm. As you type more sentences,
            the system gradually eliminates unlikely users:
          </p>
          <ul>
            <li>Samples 3-9: Users below 5% probability are eliminated</li>
            <li>Samples 10-14: Threshold increases to 10%</li>
            <li>Samples 15-19: Threshold increases to 15%</li>
            <li>And so on until a user is "confidently" identified</li>
          </ul>
        </section>

        <section className="about-section">
          <h2>Technology Stack</h2>
          <div className="tech-stack">
            <div className="tech-item">
              <span className="tech-label">Frontend</span>
              <span className="tech-value">React + TypeScript</span>
            </div>
            <div className="tech-item">
              <span className="tech-label">Backend</span>
              <span className="tech-value">.NET 10 / ASP.NET Core</span>
            </div>
            <div className="tech-item">
              <span className="tech-label">ML Framework</span>
              <span className="tech-value">ML.NET (LightGBM)</span>
            </div>
            <div className="tech-item">
              <span className="tech-label">Database</span>
              <span className="tech-value">PostgreSQL</span>
            </div>
          </div>
        </section>

        <section className="about-section">
          <h2>Privacy</h2>
          <p>
            Your typing data is only used to train and improve the identification model.
            The application does not capture the actual content you type, only the timing
            metrics between keystrokes.
          </p>
        </section>

      </div>
    </div>
  );
};

export default About;

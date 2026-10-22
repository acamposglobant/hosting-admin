import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { ApiError } from '../api/client';
import { Login } from './Login';

describe('Login', () => {
  it('renders the password form', () => {
    render(<Login onLogin={vi.fn()} />);

    expect(screen.getByLabelText('Password')).toBeInTheDocument();
    expect(screen.getByRole('button', { name: 'Sign in' })).toBeInTheDocument();
  });

  it('disables submit while the password is empty', () => {
    render(<Login onLogin={vi.fn()} />);

    expect(screen.getByRole('button', { name: 'Sign in' })).toBeDisabled();
  });

  it('calls onLogin with the entered password', async () => {
    const user = userEvent.setup();
    const onLogin = vi.fn().mockResolvedValue(undefined);
    render(<Login onLogin={onLogin} />);

    await user.type(screen.getByLabelText('Password'), 'correct horse battery staple');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    expect(onLogin).toHaveBeenCalledWith('correct horse battery staple');
  });

  it('shows a generic error when login fails', async () => {
    const user = userEvent.setup();
    const onLogin = vi.fn().mockRejectedValue(new Error('unauthorized'));
    render(<Login onLogin={onLogin} />);

    await user.type(screen.getByLabelText('Password'), 'wrong');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(screen.getByTestId('login-error')).toHaveTextContent(/login failed/i);
    });
  });

  it('shows a rate-limit specific message on 429', async () => {
    const user = userEvent.setup();
    const rateLimitError = new ApiError(429, 'rate_limited', 'Too many attempts.');
    const onLogin = vi.fn().mockRejectedValue(rateLimitError);
    render(<Login onLogin={onLogin} />);

    await user.type(screen.getByLabelText('Password'), 'whatever');
    await user.click(screen.getByRole('button', { name: 'Sign in' }));

    await waitFor(() => {
      expect(screen.getByTestId('login-error')).toHaveTextContent(/too many attempts/i);
    });
  });

  it('does not show an error while a login is pending', async () => {
    const user = userEvent.setup();
    let resolveLogin: () => void = () => {};
    const onLogin = vi.fn().mockImplementation(
      () => new Promise<void>((resolve) => {
        resolveLogin = resolve;
      }),
    );
    render(<Login onLogin={onLogin} />);

    await user.type(screen.getByLabelText('Password'), 'pw');
    const submitButton = screen.getByRole('button', { name: 'Sign in' });
    await user.click(submitButton);

    await waitFor(() => {
      expect(submitButton).toBeDisabled();
    });
    expect(screen.queryByTestId('login-error')).not.toBeInTheDocument();
    resolveLogin();
  });
});

import { Box, Container, createTheme, CssBaseline, ThemeProvider } from "@mui/material";
import NavBar from "./NavBar";
import PromoBar from "./PromoBar";
import Footer from "./Footer";
import { Outlet } from "react-router-dom";
import { useAppSelector } from "../store/store";
import { GoogleOAuthProvider } from '@react-oauth/google';
import { googleClientId, hasGoogleClientId } from "../config/env";
import ScrollToTop from "./ScrollToTop";
import { CookieConsentProvider } from "./cookieConsent";
import type { Theme } from '@mui/material/styles';
import { useGetUiSettingsQuery } from "../../features/admin/uiSettingsApi";
import ChatWidget from "../../features/chat/ChatWidget";


function App() {
  const { darkMode } = useAppSelector(state => state.ui);
  const { data: uiSettings } = useGetUiSettingsQuery();
  const palleteType = darkMode ? 'dark' : 'light';

  const buttonIconColorSetting = uiSettings?.buttonIconColor ?? 'primary';

  const resolveButtonIconColor = (theme: Theme) => {
    switch (buttonIconColorSetting) {
      case 'inherit':
        return 'inherit';
      case 'secondary':
        return theme.palette.secondary.main;
      case 'text':
        return theme.palette.text.primary;
      case 'primary':
      default:
        return theme.palette.primary.main;
    }
  };

  const theme = createTheme({
    palette: {
      mode: palleteType,
      primary: {
        main: palleteType === 'light'
          ? (uiSettings?.primaryColorLight || '#1565c0')
          : (uiSettings?.primaryColorDark || '#90caf9')
      },
      secondary: {
        main: palleteType === 'light'
          ? (uiSettings?.secondaryColorLight || '#ffb300')
          : (uiSettings?.secondaryColorDark || '#ffcc80')
      },
      background: {
        default: palleteType === 'light' ? '#ffffffff' : '#000000ff'
      }
    },
    typography: {
      h3: { fontWeight: 800, letterSpacing: -0.3 },
      h4: { fontWeight: 800, letterSpacing: -0.2 },
      h5: { fontWeight: 800, letterSpacing: -0.15 },
      h6: { fontWeight: 800, letterSpacing: -0.1 },
      subtitle1: { fontWeight: 700 },
      body1: { fontSize: '1rem' },
      body2: { fontSize: '0.95rem' },
      button: { textTransform: 'none', fontWeight: 700 }
    },
    components: {
      MuiTypography: {
        styleOverrides: {
          h2: ({ theme }) => ({
            fontWeight: 900,
            marginBottom: theme.spacing(2)
          }),
          h3: ({ theme }) => ({
            fontWeight: 900,
            marginBottom: theme.spacing(2)
          }),
          h4: ({ theme }) => ({
            fontWeight: 900,
            marginBottom: theme.spacing(2)
          }),
          h5: ({ theme }) => ({
            fontWeight: 900,
            marginBottom: theme.spacing(2)
          }),
          h6: ({ theme }) => ({
            fontWeight: 900,
            marginBottom: theme.spacing(1.5)
          })
        }
      },
      MuiButton: {
        styleOverrides: {
          startIcon: ({ ownerState, theme }) => ({
            color: ownerState.variant === 'contained' ? 'inherit' : resolveButtonIconColor(theme)
          }),
          endIcon: ({ ownerState, theme }) => ({
            color: ownerState.variant === 'contained' ? 'inherit' : resolveButtonIconColor(theme)
          })
        }
      },
      MuiPaper: {
        styleOverrides: {
          root: ({ theme }) => ({
            ...(theme.palette.mode === 'light'
              ? { border: `1px solid ${theme.palette.divider}` }
              : null)
          })
        }
      },
      MuiTableCell: {
        styleOverrides: {
          head: ({ theme }) => ({
            fontWeight: 800,
            ...(theme.palette.mode === 'light'
              ? { backgroundColor: theme.palette.action.hover }
              : null)
          })
        }
      }
    }
  });

  const appShell = (
    <ThemeProvider theme={theme}>
      <ScrollToTop />
      <CssBaseline />
      <CookieConsentProvider>
        <Box sx={{ display: 'flex', flexDirection: 'column', minHeight: '100vh' }}>
          <NavBar />
          <PromoBar />
          <Box
            sx={{
              flex: 1,
              background: darkMode
                ? 'radial-gradient(circle, #1d0d03ff, #111B27)'
                : 'radial-gradient(circle, rgb(255, 255, 255), #ffffffff)',
              py: 0,
              pb: { xs: 4, md: 6 }
            }}
          >
            <Container maxWidth='xl' sx={{ mt: { xs: 2, sm: 3 } }}>
              <Outlet />
            </Container>
          </Box>
          <ChatWidget />
          <Footer />
        </Box>
      </CookieConsentProvider>
    </ThemeProvider>
  );

  return hasGoogleClientId ? (
    <GoogleOAuthProvider clientId={googleClientId!}>
      {appShell}
    </GoogleOAuthProvider>
  ) : (
    appShell
  );
}

export default App

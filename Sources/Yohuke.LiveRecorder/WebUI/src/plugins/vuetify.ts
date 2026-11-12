import 'vuetify/styles'
import '@mdi/font/css/materialdesignicons.css'
import { createVuetify } from 'vuetify'

export default createVuetify({
  theme: {
    defaultTheme: 'night',
    themes: {
      night: {
        dark: true,
        colors: {
          primary: '#FF765E', secondary: '#A6B4D4', background: '#101216', surface: '#191C23',
          'surface-variant': '#242A34', 'on-background': '#F3F4F7', 'on-surface': '#F3F4F7',
          error: '#FF858C', success: '#80D7AB', warning: '#F0C580', info: '#91B9F7',
        },
      },
      day: {
        dark: false,
        colors: {
          primary: '#D94E38', secondary: '#5D6B8A', background: '#F3F4F7', surface: '#FFFFFF',
          'surface-variant': '#E9ECF2', error: '#BB2E40', success: '#21835C', warning: '#9B6619', info: '#3973B9',
        },
      },
    },
  },
  defaults: {
    VBtn: { rounded: 'lg', variant: 'flat', style: 'text-transform: none; letter-spacing: 0' },
    VCard: { rounded: 'xl', elevation: 0 },
    VTextField: { variant: 'outlined', density: 'comfortable', color: 'primary', hideDetails: 'auto' },
    VTextarea: { variant: 'outlined', color: 'primary', hideDetails: 'auto' },
    VSelect: { variant: 'outlined', density: 'comfortable', color: 'primary', hideDetails: 'auto' },
    VAutocomplete: { variant: 'outlined', density: 'comfortable', color: 'primary', hideDetails: 'auto' },
    VSwitch: { color: 'primary', hideDetails: true, inset: true },
    VCheckbox: { color: 'primary', hideDetails: true },
    VAlert: { rounded: 'lg', variant: 'tonal' },
    VDialog: { maxWidth: 560 },
    VChip: { rounded: 'lg', size: 'small', variant: 'tonal' },
  },
})

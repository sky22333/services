import React from 'react'
import {createRoot} from 'react-dom/client'
import './style.css'
import App from './App'
import { FluentProvider, webLightTheme } from '@fluentui/react-components'

const container = document.getElementById('root')
const root = createRoot(container)

root.render(
    <React.StrictMode>
        <FluentProvider theme={webLightTheme}>
            <App/>
        </FluentProvider>
    </React.StrictMode>
)
